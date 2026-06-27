#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/avutil.h>
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libavutil/pixfmt.h>
#include <libswscale/swscale.h>
#include <dlfcn.h>
#include <fcntl.h>
#include <linux/videodev2.h>
#include <pthread.h>
#include <sys/ioctl.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define STRINGIFY_INNER(x) #x
#define STRINGIFY(x) STRINGIFY_INNER(x)

typedef struct DbLinuxFrame {
  int32_t status;
  int32_t fd;
  uint32_t width;
  uint32_t height;
  uint32_t fourcc;
  uint32_t offset;
  int32_t stride;
} DbLinuxFrame;

typedef struct DbLinuxStreamInfo {
  int32_t running;
  int32_t broken;
  int32_t width;
  int32_t height;
  int64_t write_pos;
  int64_t keyframe_pos;
  int64_t frames;
} DbLinuxStreamInfo;

typedef struct NativeStream {
  pthread_mutex_t lock;
  int running;
  int broken;
  int width;
  int height;
  int fps;
  int bitrate_mbps;
  int max_res;
  uint32_t adapter_vendor_id;
  char encoder_pref[32];
  char codec_name[64];
  int64_t pts;
  int64_t frames;
  int64_t write_pos;
  int64_t keyframe_pos;
  int64_t packet_start_pos;
  int pending_keyframe;
  uint8_t *ring;
  int ring_size;
  AVFormatContext *fmt;
  AVCodecContext *codec;
  AVStream *stream;
  AVFrame *frame;
  AVPacket *packet;
  AVIOContext *avio;
  uint8_t *avio_buffer;
  struct SwsContext *sws;
  uint8_t *src_rows;
  int src_rows_size;

  int audio_enabled;
  int has_audio;
  int a_sample_rate;
  int a_channels;
  int a_frame_size;
  int64_t a_pts;
  AVCodecContext *acodec;
  AVStream *astream;
  AVFrame *aframe;
  AVPacket *apacket;
  float *a_fifo;
  int a_fifo_samples;
  int a_fifo_cap_samples;
  pthread_mutex_t mux_lock;
} NativeStream;

static const int RING_SIZE = 16 * 1024 * 1024;
static const int AVIO_BUFFER_SIZE = 64 * 1024;
static pthread_mutex_t g_streams_lock = PTHREAD_MUTEX_INITIALIZER;
static NativeStream *g_streams[256];
static uint64_t g_next_stream_id = 1;
static char g_last_error[512];

typedef struct FfmpegApi {
  void *avutil;
  void *avcodec;
  void *avformat;
  void *swscale;
  int loaded;
  int failed;

  int (*av_dict_set)(AVDictionary **pm, const char *key, const char *value, int flags);
  void (*av_dict_free)(AVDictionary **m);
  int (*av_strerror)(int errnum, char *errbuf, size_t errbuf_size);
  void *(*av_malloc)(size_t size);
  AVFrame *(*av_frame_alloc)(void);
  void (*av_frame_free)(AVFrame **frame);
  int (*av_frame_get_buffer)(AVFrame *frame, int align);
  int (*av_frame_make_writable)(AVFrame *frame);
  void (*av_channel_layout_default)(AVChannelLayout *ch_layout, int nb_channels);

  const AVCodec *(*avcodec_find_encoder_by_name)(const char *name);
  const AVCodec *(*avcodec_find_encoder)(enum AVCodecID id);
  AVCodecContext *(*avcodec_alloc_context3)(const AVCodec *codec);
  int (*avcodec_open2)(AVCodecContext *avctx, const AVCodec *codec, AVDictionary **options);
  void (*avcodec_free_context)(AVCodecContext **avctx);
  int (*avcodec_parameters_from_context)(AVCodecParameters *par, const AVCodecContext *codec);
  int (*avcodec_send_frame)(AVCodecContext *avctx, const AVFrame *frame);
  int (*avcodec_receive_packet)(AVCodecContext *avctx, AVPacket *avpkt);
  AVPacket *(*av_packet_alloc)(void);
  void (*av_packet_free)(AVPacket **pkt);
  void (*av_packet_rescale_ts)(AVPacket *pkt, AVRational tb_src, AVRational tb_dst);
  void (*av_packet_unref)(AVPacket *pkt);

  int (*avformat_alloc_output_context2)(AVFormatContext **ctx, const AVOutputFormat *oformat, const char *format_name, const char *filename);
  AVStream *(*avformat_new_stream)(AVFormatContext *s, const AVCodec *c);
  int (*avformat_write_header)(AVFormatContext *s, AVDictionary **options);
  int (*av_interleaved_write_frame)(AVFormatContext *s, AVPacket *pkt);
  int (*av_write_trailer)(AVFormatContext *s);
  void (*avformat_free_context)(AVFormatContext *s);
  AVIOContext *(*avio_alloc_context)(unsigned char *buffer, int buffer_size, int write_flag, void *opaque,
                                     int (*read_packet)(void *opaque, uint8_t *buf, int buf_size),
                                     int (*write_packet)(void *opaque, const uint8_t *buf, int buf_size),
                                     int64_t (*seek)(void *opaque, int64_t offset, int whence));
  void (*avio_context_free)(AVIOContext **s);

  struct SwsContext *(*sws_getContext)(int srcW, int srcH, enum AVPixelFormat srcFormat, int dstW, int dstH, enum AVPixelFormat dstFormat,
                                       int flags, SwsFilter *srcFilter, SwsFilter *dstFilter, const double *param);
  int (*sws_scale)(struct SwsContext *c, const uint8_t *const srcSlice[], const int srcStride[], int srcSliceY, int srcSliceH,
                   uint8_t *const dst[], const int dstStride[]);
  void (*sws_freeContext)(struct SwsContext *swsContext);
} FfmpegApi;

static FfmpegApi g_ff;
static pthread_mutex_t g_ff_lock = PTHREAD_MUTEX_INITIALIZER;

static const char *const k_library_dirs[] = {
    "/run/host/usr/lib",
    "/run/host/usr/lib64",
    "/run/host/usr/local/lib",
    "/run/host/usr/local/lib64",
    "/run/host/usr/lib/x86_64-linux-gnu",
    "/run/host/usr/lib/pulseaudio",
    "/run/host/lib/x86_64-linux-gnu",
    "/run/host/lib64",
    "/run/host/lib",
    "/usr/lib",
    "/usr/lib64",
    "/usr/local/lib",
    "/usr/local/lib64",
    "/usr/lib/x86_64-linux-gnu",
    "/lib/x86_64-linux-gnu",
    "/lib64",
    "/lib",
};

static const char *const k_host_library_path =
    "/run/host/usr/lib:"
    "/run/host/usr/lib64:"
    "/run/host/usr/lib/x86_64-linux-gnu:"
    "/run/host/usr/lib/pulseaudio:"
    "/run/host/lib:"
    "/run/host/lib64:"
    "/run/host/lib/x86_64-linux-gnu";

static void set_last_error(const char *message, const char *detail) {
  snprintf(g_last_error, sizeof(g_last_error), "%s%s%s", message ? message : "",
           detail ? ": " : "", detail ? detail : "");
}

static int is_absolute_path(const char *path) {
  return path && path[0] == '/';
}

static void preload_ldd_dependencies(const char *path) {
  if (!is_absolute_path(path))
    return;

  char command[8192];
  int written = snprintf(command, sizeof(command), "LD_LIBRARY_PATH='%s${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}' ldd '%s' 2>/dev/null", k_host_library_path, path);
  if (written <= 0 || (size_t)written >= sizeof(command))
    return;

  FILE *pipe = popen(command, "r");
  if (!pipe)
    return;

  char deps[256][4096];
  size_t count = 0;
  char line[4096];
  while (count < 256 && fgets(line, sizeof(line), pipe)) {
    char *dep = NULL;
    char *arrow = strstr(line, "=>");
    if (arrow) {
      dep = arrow + 2;
      while (*dep == ' ' || *dep == '\t')
        dep++;
    } else {
      dep = line;
      while (*dep == ' ' || *dep == '\t')
        dep++;
      if (*dep != '/')
        continue;
    }

    char *end = dep;
    while (*end && *end != ' ' && *end != '\t' && *end != '\n' && *end != '\r')
      end++;
    *end = '\0';
    if (!is_absolute_path(dep))
      continue;
    strncpy(deps[count], dep, sizeof(deps[count]) - 1);
    deps[count][sizeof(deps[count]) - 1] = '\0';
    count++;
  }
  pclose(pipe);

  while (count > 0) {
    count--;
    dlopen(deps[count], RTLD_NOW | RTLD_GLOBAL);
  }
}

static int parse_missing_dependency(const char *error, char *dependency, size_t dependency_size) {
  if (!error || !dependency || dependency_size == 0)
    return 0;

  const char *suffix = strstr(error, ": cannot open shared object file");
  if (!suffix || suffix == error)
    return 0;

  size_t len = (size_t)(suffix - error);
  const char *start = error;
  for (size_t i = 0; i < len; i++) {
    if (error[i] == '/')
      start = error + i + 1;
  }
  len = (size_t)(suffix - start);
  if (len == 0 || len >= dependency_size || !strstr(start, ".so"))
    return 0;

  memcpy(dependency, start, len);
  dependency[len] = '\0';
  return 1;
}

static void *try_dlopen_path_depth(const char *path, int depth);

static void *try_dlopen_in_dir_depth(const char *dir, const char *name, int depth) {
  if (!dir || !dir[0] || !name || !name[0])
    return NULL;
  char path[4096];
  int written = snprintf(path, sizeof(path), "%s/%s", dir, name);
  if (written <= 0 || (size_t)written >= sizeof(path))
    return NULL;
  return try_dlopen_path_depth(path, depth);
}

static void *try_load_dependency_name(const char *name, int depth) {
  if (!name || !name[0] || depth > 64)
    return NULL;

  const char *override_dir = getenv("DESKTOPBUDDY_FFMPEG_DIR");
  if (override_dir) {
    void *handle = try_dlopen_in_dir_depth(override_dir, name, depth + 1);
    if (handle)
      return handle;
  }

  for (size_t d = 0; d < sizeof(k_library_dirs) / sizeof(k_library_dirs[0]); d++) {
    void *handle = try_dlopen_in_dir_depth(k_library_dirs[d], name, depth + 1);
    if (handle)
      return handle;
  }

  return NULL;
}

static void *try_dlopen_path_depth(const char *path, int depth) {
  if (!path || !path[0])
    return NULL;
  preload_ldd_dependencies(path);
  void *handle = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
  if (handle)
    return handle;

  const char *error = dlerror();
  char error_copy[512];
  snprintf(error_copy, sizeof(error_copy), "%s", error ? error : "");

  char dependency[256];
  if (depth < 64 && parse_missing_dependency(error_copy, dependency, sizeof(dependency)) &&
      try_load_dependency_name(dependency, depth + 1)) {
    handle = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (handle)
      return handle;
    error = dlerror();
    snprintf(error_copy, sizeof(error_copy), "%s", error ? error : "");
  }

  set_last_error("dlopen failed", error_copy);
  return handle;
}

static void *try_dlopen_path(const char *path) {
  return try_dlopen_path_depth(path, 0);
}

static void *try_dlopen_in_dir(const char *dir, const char *name) {
  return try_dlopen_in_dir_depth(dir, name, 0);
}

static void *try_ldconfig(const char *name) {
  FILE *pipe = popen("ldconfig -p 2>/dev/null", "r");
  if (!pipe)
    return NULL;

  char line[4096];
  void *handle = NULL;
  while (fgets(line, sizeof(line), pipe)) {
    if (!strstr(line, name))
      continue;
    char *arrow = strstr(line, "=>");
    if (!arrow)
      continue;
    char *path = arrow + 2;
    while (*path == ' ' || *path == '\t')
      path++;
    char *end = path + strlen(path);
    while (end > path && (end[-1] == '\n' || end[-1] == '\r' || end[-1] == ' ' || end[-1] == '\t'))
      *--end = '\0';
    handle = try_dlopen_path(path);
    if (handle)
      break;
  }

  pclose(pipe);
  return handle;
}

static void *load_library_candidates(const char *const *names, size_t count) {
  const char *override_dir = getenv("DESKTOPBUDDY_FFMPEG_DIR");

  for (size_t i = 0; i < count; i++) {
    if (override_dir) {
      void *handle = try_dlopen_in_dir(override_dir, names[i]);
      if (handle)
        return handle;
    }

    for (size_t d = 0; d < sizeof(k_library_dirs) / sizeof(k_library_dirs[0]); d++) {
      void *handle = try_dlopen_in_dir(k_library_dirs[d], names[i]);
      if (handle)
        return handle;
    }

    void *handle = try_ldconfig(names[i]);
    if (handle)
      return handle;

    handle = try_dlopen_path(names[i]);
    if (handle)
      return handle;
  }

  return NULL;
}

static int load_symbol(void *library, const char *name, void **target) {
  *target = dlsym(library, name);
  return *target ? 0 : -1;
}

static int ensure_ffmpeg_loaded(void) {
  pthread_mutex_lock(&g_ff_lock);
  if (g_ff.loaded) {
    pthread_mutex_unlock(&g_ff_lock);
    return 0;
  }
  if (g_ff.failed) {
    pthread_mutex_unlock(&g_ff_lock);
    return -1;
  }

  const char *avutil_names[] = {"libavutil.so." STRINGIFY(LIBAVUTIL_VERSION_MAJOR), "libavutil.so"};
  const char *avcodec_names[] = {"libavcodec.so." STRINGIFY(LIBAVCODEC_VERSION_MAJOR), "libavcodec.so"};
  const char *avformat_names[] = {"libavformat.so." STRINGIFY(LIBAVFORMAT_VERSION_MAJOR), "libavformat.so"};
  const char *swscale_names[] = {"libswscale.so." STRINGIFY(LIBSWSCALE_VERSION_MAJOR), "libswscale.so"};

  g_ff.avutil = load_library_candidates(avutil_names, sizeof(avutil_names) / sizeof(avutil_names[0]));
  g_ff.avcodec = load_library_candidates(avcodec_names, sizeof(avcodec_names) / sizeof(avcodec_names[0]));
  g_ff.avformat = load_library_candidates(avformat_names, sizeof(avformat_names) / sizeof(avformat_names[0]));
  g_ff.swscale = load_library_candidates(swscale_names, sizeof(swscale_names) / sizeof(swscale_names[0]));
  if (!g_ff.avutil || !g_ff.avcodec || !g_ff.avformat || !g_ff.swscale) {
    if (!g_last_error[0])
      set_last_error("missing FFmpeg library", NULL);
    goto fail;
  }

#define LOAD_AVUTIL(name) if (load_symbol(g_ff.avutil, #name, (void **)&g_ff.name) != 0) goto fail
#define LOAD_AVCODEC(name) if (load_symbol(g_ff.avcodec, #name, (void **)&g_ff.name) != 0) goto fail
#define LOAD_AVFORMAT(name) if (load_symbol(g_ff.avformat, #name, (void **)&g_ff.name) != 0) goto fail
#define LOAD_SWSCALE(name) if (load_symbol(g_ff.swscale, #name, (void **)&g_ff.name) != 0) goto fail

  LOAD_AVUTIL(av_dict_set);
  LOAD_AVUTIL(av_dict_free);
  LOAD_AVUTIL(av_strerror);
  LOAD_AVUTIL(av_malloc);
  LOAD_AVUTIL(av_frame_alloc);
  LOAD_AVUTIL(av_frame_free);
  LOAD_AVUTIL(av_frame_get_buffer);
  LOAD_AVUTIL(av_frame_make_writable);
  LOAD_AVUTIL(av_channel_layout_default);
  LOAD_AVCODEC(avcodec_find_encoder_by_name);
  LOAD_AVCODEC(avcodec_find_encoder);
  LOAD_AVCODEC(avcodec_alloc_context3);
  LOAD_AVCODEC(avcodec_open2);
  LOAD_AVCODEC(avcodec_free_context);
  LOAD_AVCODEC(avcodec_parameters_from_context);
  LOAD_AVCODEC(avcodec_send_frame);
  LOAD_AVCODEC(avcodec_receive_packet);
  LOAD_AVCODEC(av_packet_alloc);
  LOAD_AVCODEC(av_packet_free);
  LOAD_AVCODEC(av_packet_rescale_ts);
  LOAD_AVCODEC(av_packet_unref);
  LOAD_AVFORMAT(avformat_alloc_output_context2);
  LOAD_AVFORMAT(avformat_new_stream);
  LOAD_AVFORMAT(avformat_write_header);
  LOAD_AVFORMAT(av_interleaved_write_frame);
  LOAD_AVFORMAT(av_write_trailer);
  LOAD_AVFORMAT(avformat_free_context);
  LOAD_AVFORMAT(avio_alloc_context);
  LOAD_AVFORMAT(avio_context_free);
  LOAD_SWSCALE(sws_getContext);
  LOAD_SWSCALE(sws_scale);
  LOAD_SWSCALE(sws_freeContext);

#undef LOAD_AVUTIL
#undef LOAD_AVCODEC
#undef LOAD_AVFORMAT
#undef LOAD_SWSCALE

  g_ff.loaded = 1;
  pthread_mutex_unlock(&g_ff_lock);
  return 0;

fail:
  if (!g_last_error[0])
    set_last_error("missing FFmpeg symbol", dlerror());
  g_ff.failed = 1;
  pthread_mutex_unlock(&g_ff_lock);
  return -1;
}

#define av_dict_set g_ff.av_dict_set
#define av_dict_free g_ff.av_dict_free
#define av_strerror g_ff.av_strerror
#define av_malloc g_ff.av_malloc
#define av_frame_alloc g_ff.av_frame_alloc
#define av_frame_free g_ff.av_frame_free
#define av_frame_get_buffer g_ff.av_frame_get_buffer
#define av_frame_make_writable g_ff.av_frame_make_writable
#define av_channel_layout_default g_ff.av_channel_layout_default
#define avcodec_find_encoder_by_name g_ff.avcodec_find_encoder_by_name
#define avcodec_find_encoder g_ff.avcodec_find_encoder
#define avcodec_alloc_context3 g_ff.avcodec_alloc_context3
#define avcodec_open2 g_ff.avcodec_open2
#define avcodec_free_context g_ff.avcodec_free_context
#define avcodec_parameters_from_context g_ff.avcodec_parameters_from_context
#define avcodec_send_frame g_ff.avcodec_send_frame
#define avcodec_receive_packet g_ff.avcodec_receive_packet
#define av_packet_alloc g_ff.av_packet_alloc
#define av_packet_free g_ff.av_packet_free
#define av_packet_rescale_ts g_ff.av_packet_rescale_ts
#define av_packet_unref g_ff.av_packet_unref
#define avformat_alloc_output_context2 g_ff.avformat_alloc_output_context2
#define avformat_new_stream g_ff.avformat_new_stream
#define avformat_write_header g_ff.avformat_write_header
#define av_interleaved_write_frame g_ff.av_interleaved_write_frame
#define av_write_trailer g_ff.av_write_trailer
#define avformat_free_context g_ff.avformat_free_context
#define avio_alloc_context g_ff.avio_alloc_context
#define avio_context_free g_ff.avio_context_free
#define sws_getContext g_ff.sws_getContext
#define sws_scale g_ff.sws_scale
#define sws_freeContext g_ff.sws_freeContext

static int clamp_even(int value) {
  if (value < 2)
    return 2;
  return value & ~1;
}

static int is_encoder_name(const char *value) {
  return value &&
         (!strcmp(value, "hevc_nvenc") || !strcmp(value, "h264_nvenc") ||
          !strcmp(value, "hevc_amf") || !strcmp(value, "h264_amf") ||
          !strcmp(value, "hevc_qsv") || !strcmp(value, "h264_qsv") ||
          !strcmp(value, "libx264") || !strcmp(value, "libx265"));
}

static void append_encoder(const char **list, int *count, int max_count, const char *name) {
  if (!name || !name[0] || *count >= max_count)
    return;
  for (int i = 0; i < *count; i++) {
    if (!strcmp(list[i], name))
      return;
  }
  list[(*count)++] = name;
}

static int build_encoder_list(uint32_t adapter_vendor_id, const char *configured, const char **list, int max_count) {
  int count = 0;
  if (configured && strcmp(configured, "auto") && is_encoder_name(configured))
    append_encoder(list, &count, max_count, configured);

  switch (adapter_vendor_id) {
    case 0x10DE:
      append_encoder(list, &count, max_count, "hevc_nvenc");
      append_encoder(list, &count, max_count, "h264_nvenc");
      append_encoder(list, &count, max_count, "hevc_qsv");
      append_encoder(list, &count, max_count, "h264_qsv");
      append_encoder(list, &count, max_count, "hevc_amf");
      append_encoder(list, &count, max_count, "h264_amf");
      break;
    case 0x1002:
      append_encoder(list, &count, max_count, "hevc_amf");
      append_encoder(list, &count, max_count, "h264_amf");
      append_encoder(list, &count, max_count, "hevc_qsv");
      append_encoder(list, &count, max_count, "h264_qsv");
      append_encoder(list, &count, max_count, "hevc_nvenc");
      append_encoder(list, &count, max_count, "h264_nvenc");
      break;
    case 0x8086:
      append_encoder(list, &count, max_count, "hevc_qsv");
      append_encoder(list, &count, max_count, "h264_qsv");
      append_encoder(list, &count, max_count, "hevc_nvenc");
      append_encoder(list, &count, max_count, "h264_nvenc");
      append_encoder(list, &count, max_count, "hevc_amf");
      append_encoder(list, &count, max_count, "h264_amf");
      break;
    default:
      append_encoder(list, &count, max_count, "hevc_qsv");
      append_encoder(list, &count, max_count, "h264_qsv");
      append_encoder(list, &count, max_count, "hevc_nvenc");
      append_encoder(list, &count, max_count, "hevc_amf");
      append_encoder(list, &count, max_count, "h264_nvenc");
      append_encoder(list, &count, max_count, "h264_amf");
      break;
  }

  append_encoder(list, &count, max_count, "libx265");
  append_encoder(list, &count, max_count, "libx264");
  return count;
}

static void free_encoder_attempt(NativeStream *s) {
  if (s->sws) {
    sws_freeContext(s->sws);
    s->sws = NULL;
  }
  if (s->packet) {
    av_packet_free(&s->packet);
    s->packet = NULL;
  }
  if (s->frame) {
    av_frame_free(&s->frame);
    s->frame = NULL;
  }
  if (s->codec) {
    avcodec_free_context(&s->codec);
    s->codec = NULL;
  }
  if (s->apacket) {
    av_packet_free(&s->apacket);
    s->apacket = NULL;
  }
  if (s->aframe) {
    av_frame_free(&s->aframe);
    s->aframe = NULL;
  }
  if (s->acodec) {
    avcodec_free_context(&s->acodec);
    s->acodec = NULL;
  }
  if (s->fmt) {
    if (s->fmt->pb)
      avio_context_free(&s->fmt->pb);
    avformat_free_context(s->fmt);
    s->fmt = NULL;
  }
  s->stream = NULL;
  s->astream = NULL;
  s->has_audio = 0;
  s->a_pts = 0;
  s->a_fifo_samples = 0;
  s->avio = NULL;
  s->avio_buffer = NULL;
}

static int open_audio_encoder(NativeStream *s) {
  if (!s->audio_enabled)
    return 0;

  int sample_rate = s->a_sample_rate > 0 ? s->a_sample_rate : 48000;
  int channels = s->a_channels > 0 ? s->a_channels : 2;

  const AVCodec *codec = avcodec_find_encoder_by_name("aac");
  if (!codec)
    return 0;

  s->acodec = avcodec_alloc_context3(codec);
  if (!s->acodec)
    return 0;

  s->acodec->sample_rate = sample_rate;
  s->acodec->sample_fmt = AV_SAMPLE_FMT_FLTP;
  s->acodec->bit_rate = 128000;
  s->acodec->time_base = (AVRational){1, sample_rate};
  av_channel_layout_default(&s->acodec->ch_layout, channels);

  if (avcodec_open2(s->acodec, codec, NULL) < 0) {
    avcodec_free_context(&s->acodec);
    s->acodec = NULL;
    return 0;
  }

  s->astream = avformat_new_stream(s->fmt, NULL);
  if (!s->astream) {
    avcodec_free_context(&s->acodec);
    s->acodec = NULL;
    return 0;
  }
  s->astream->time_base = s->acodec->time_base;
  if (avcodec_parameters_from_context(s->astream->codecpar, s->acodec) < 0) {
    avcodec_free_context(&s->acodec);
    s->acodec = NULL;
    s->astream = NULL;
    return 0;
  }

  s->aframe = av_frame_alloc();
  s->apacket = av_packet_alloc();
  if (!s->aframe || !s->apacket) {
    free_encoder_attempt(s);
    return 0;
  }
  s->a_frame_size = s->acodec->frame_size > 0 ? s->acodec->frame_size : 1024;
  s->aframe->format = AV_SAMPLE_FMT_FLTP;
  s->aframe->sample_rate = sample_rate;
  s->aframe->nb_samples = s->a_frame_size;
  av_channel_layout_default(&s->aframe->ch_layout, channels);
  if (av_frame_get_buffer(s->aframe, 0) < 0) {
    av_frame_free(&s->aframe);
    av_packet_free(&s->apacket);
    avcodec_free_context(&s->acodec);
    s->aframe = NULL;
    s->apacket = NULL;
    s->acodec = NULL;
    s->astream = NULL;
    return 0;
  }

  s->a_sample_rate = sample_rate;
  s->a_channels = channels;
  s->a_pts = 0;
  s->has_audio = 1;
  return 0;
}

static void ring_write(NativeStream *s, const uint8_t *data, int len) {
  if (!s || !data || len <= 0 || !s->ring)
    return;

  if (s->pending_keyframe) {
    s->keyframe_pos = s->write_pos;
    s->pending_keyframe = 0;
  }

  int offset = (int)(s->write_pos % s->ring_size);
  int first = s->ring_size - offset;
  if (first > len)
    first = len;
  memcpy(s->ring + offset, data, (size_t)first);
  if (len > first)
    memcpy(s->ring, data + first, (size_t)(len - first));
  s->write_pos += len;
}

static int write_packet(void *opaque, const uint8_t *buf, int buf_size) {
  NativeStream *s = (NativeStream *)opaque;
  pthread_mutex_lock(&s->lock);
  ring_write(s, buf, buf_size);
  pthread_mutex_unlock(&s->lock);
  return buf_size;
}

static int try_open_encoder(NativeStream *s, const char *encoder_name, int src_w, int src_h, int width, int height, int fps, int bitrate_mbps) {
  const AVCodec *codec = avcodec_find_encoder_by_name(encoder_name);
  if (!codec) {
    set_last_error("encoder unavailable", encoder_name);
    return -1;
  }

  s->codec = avcodec_alloc_context3(codec);
  if (!s->codec)
    return -2;

  s->codec->width = width;
  s->codec->height = height;
  s->codec->time_base = (AVRational){1, fps};
  s->codec->framerate = (AVRational){fps, 1};
  s->codec->gop_size = fps;
  s->codec->max_b_frames = 0;
  s->codec->pix_fmt = AV_PIX_FMT_YUV420P;
  s->codec->bit_rate = (int64_t)(bitrate_mbps > 0 ? bitrate_mbps : 8) * 1000 * 1000;

  AVDictionary *opts = NULL;
  if (strstr(encoder_name, "nvenc")) {
    av_dict_set(&opts, "preset", "p1", 0);
    av_dict_set(&opts, "tune", "ull", 0);
    av_dict_set(&opts, "rc", "vbr", 0);
    av_dict_set(&opts, "zerolatency", "1", 0);
    av_dict_set(&opts, "delay", "0", 0);
    av_dict_set(&opts, "rc-lookahead", "0", 0);
    av_dict_set(&opts, "forced-idr", "1", 0);
    av_dict_set(&opts, "repeat-headers", "1", 0);
  } else if (strstr(encoder_name, "amf")) {
    av_dict_set(&opts, "usage", "lowlatency_high_quality", 0);
    av_dict_set(&opts, "rc", "vbr_peak", 0);
    av_dict_set(&opts, "header_insertion_mode", "idr", 0);
  } else if (!strcmp(encoder_name, "libx264")) {
    av_dict_set(&opts, "preset", "veryfast", 0);
    av_dict_set(&opts, "tune", "zerolatency", 0);
    av_dict_set(&opts, "profile", "baseline", 0);
  } else if (!strcmp(encoder_name, "libx265")) {
    av_dict_set(&opts, "preset", "ultrafast", 0);
    av_dict_set(&opts, "tune", "zerolatency", 0);
  }

  int ret = avcodec_open2(s->codec, codec, &opts);
  av_dict_free(&opts);
  if (ret < 0) {
    char error[256];
    if (av_strerror && av_strerror(ret, error, sizeof(error)) == 0) {
      char detail[384];
      snprintf(detail, sizeof(detail), "%s: %s", encoder_name, error);
      set_last_error("avcodec_open2 failed", detail);
    }
    else
      set_last_error("avcodec_open2 failed", encoder_name);
    return -3;
  }

  ret = avformat_alloc_output_context2(&s->fmt, NULL, "mpegts", NULL);
  if (ret < 0 || !s->fmt)
    return -4;

  s->stream = avformat_new_stream(s->fmt, NULL);
  if (!s->stream)
    return -5;
  s->stream->time_base = s->codec->time_base;
  ret = avcodec_parameters_from_context(s->stream->codecpar, s->codec);
  if (ret < 0)
    return -6;

  s->avio_buffer = av_malloc(AVIO_BUFFER_SIZE);
  if (!s->avio_buffer)
    return -7;
  s->avio = avio_alloc_context(s->avio_buffer, AVIO_BUFFER_SIZE, 1, s, NULL, write_packet, NULL);
  if (!s->avio)
    return -8;
  s->avio_buffer = NULL;
  s->fmt->pb = s->avio;
  s->fmt->flags |= AVFMT_FLAG_CUSTOM_IO;

  open_audio_encoder(s);

  AVDictionary *muxer_opts = NULL;
  av_dict_set(&muxer_opts, "mpegts_flags", "pat_pmt_at_frames", 0);
  ret = avformat_write_header(s->fmt, &muxer_opts);
  av_dict_free(&muxer_opts);
  if (ret < 0)
    return -9;

  s->frame = av_frame_alloc();
  s->packet = av_packet_alloc();
  if (!s->frame || !s->packet)
    return -10;
  s->frame->format = s->codec->pix_fmt;
  s->frame->width = width;
  s->frame->height = height;
  ret = av_frame_get_buffer(s->frame, 32);
  if (ret < 0)
    return -11;

  s->sws = sws_getContext(src_w, src_h, AV_PIX_FMT_BGRA, width, height, AV_PIX_FMT_YUV420P,
                          SWS_FAST_BILINEAR, NULL, NULL, NULL);
  if (!s->sws)
    return -12;

  snprintf(s->codec_name, sizeof(s->codec_name), "%s", encoder_name);
  return 0;
}

static int open_encoder(NativeStream *s, int src_w, int src_h, int width, int height, int fps, int bitrate_mbps) {
  if (ensure_ffmpeg_loaded() != 0)
    return -100;

  s->width = width;
  s->height = height;

  const char *encoders[10];
  int encoder_count = build_encoder_list(s->adapter_vendor_id, s->encoder_pref[0] ? s->encoder_pref : "auto", encoders, 10);
  int last_result = -1;
  for (int i = 0; i < encoder_count; i++) {
    free_encoder_attempt(s);
    last_result = try_open_encoder(s, encoders[i], src_w, src_h, width, height, fps, bitrate_mbps);
    if (last_result == 0)
      return 0;
  }

  free_encoder_attempt(s);
  return last_result;
}

__attribute__((visibility("default")))
void *db_native_stream_create(int src_w, int src_h, int fps, int bitrate_mbps, int max_res, const char *rtsp_url) {
  (void)rtsp_url;
  NativeStream *s = calloc(1, sizeof(NativeStream));
  if (!s)
    return NULL;

  pthread_mutex_init(&s->lock, NULL);
  pthread_mutex_init(&s->mux_lock, NULL);
  s->fps = fps > 0 ? fps : 30;
  int width = clamp_even(src_w);
  int height = clamp_even(src_h);
  if (max_res > 0 && (width > max_res || height > max_res)) {
    double scale = width >= height ? (double)max_res / (double)width : (double)max_res / (double)height;
    width = clamp_even((int)(width * scale));
    height = clamp_even((int)(height * scale));
  }

  s->width = width;
  s->height = height;
  s->bitrate_mbps = bitrate_mbps;
  s->max_res = max_res;
  snprintf(s->encoder_pref, sizeof(s->encoder_pref), "auto");
  s->ring_size = RING_SIZE;
  s->keyframe_pos = -1;
  s->ring = malloc((size_t)s->ring_size);
  if (!s->ring) {
    s->broken = 1;
    return s;
  }

  if (open_encoder(s, src_w, src_h, width, height, s->fps, bitrate_mbps) != 0) {
    s->broken = 1;
    return s;
  }

  s->running = 1;
  return s;
}

static int ensure_src_buffer(NativeStream *s, int size) {
  if (size <= s->src_rows_size)
    return 0;
  uint8_t *next = realloc(s->src_rows, (size_t)size);
  if (!next)
    return -1;
  s->src_rows = next;
  s->src_rows_size = size;
  return 0;
}

__attribute__((visibility("default")))
int db_native_stream_push_memfd(void *handle, const DbLinuxFrame *frame) {
  NativeStream *s = (NativeStream *)handle;
  if (!s || !frame || s->broken || frame->fd < 0)
    return -1;
  if (frame->width == 0 || frame->height == 0 || frame->stride <= 0)
    return -2;

  int src_w = clamp_even((int)frame->width);
  int src_h = clamp_even((int)frame->height);
  int row_bytes = src_w * 4;
  if (row_bytes > frame->stride)
    return -3;

  if (!s->codec) {
    int out_w = src_w;
    int out_h = src_h;
    if (s->max_res > 0 && (out_w > s->max_res || out_h > s->max_res)) {
      double scale = out_w >= out_h ? (double)s->max_res / (double)out_w : (double)s->max_res / (double)out_h;
      out_w = clamp_even((int)(out_w * scale));
      out_h = clamp_even((int)(out_h * scale));
    }
    int ret = open_encoder(s, src_w, src_h, out_w, out_h, s->fps, s->bitrate_mbps);
    if (ret != 0) {
      s->broken = 1;
      return -30 + ret;
    }
    s->running = 1;
  }

  int src_size = row_bytes * src_h;
  if (ensure_src_buffer(s, src_size) != 0)
    return -4;

  for (int y = 0; y < src_h; y++) {
    off_t offset = (off_t)frame->offset + (off_t)y * (off_t)frame->stride;
    uint8_t *dst = s->src_rows + y * row_bytes;
    int copied = 0;
    while (copied < row_bytes) {
      ssize_t n = pread(frame->fd, dst + copied, (size_t)(row_bytes - copied), offset + copied);
      if (n <= 0)
        return -5;
      copied += (int)n;
    }
  }

  if (av_frame_make_writable(s->frame) < 0)
    return -6;

  const uint8_t *src_slices[4] = {s->src_rows, NULL, NULL, NULL};
  int src_strides[4] = {row_bytes, 0, 0, 0};
  sws_scale(s->sws, src_slices, src_strides, 0, src_h, s->frame->data, s->frame->linesize);
  s->frame->pts = s->pts++;

  int ret = avcodec_send_frame(s->codec, s->frame);
  if (ret < 0) {
    s->broken = 1;
    return -7;
  }

  while ((ret = avcodec_receive_packet(s->codec, s->packet)) == 0) {
    av_packet_rescale_ts(s->packet, s->codec->time_base, s->stream->time_base);
    s->packet->stream_index = s->stream->index;
    pthread_mutex_lock(&s->lock);
    if ((s->packet->flags & AV_PKT_FLAG_KEY) != 0)
      s->pending_keyframe = 1;
    pthread_mutex_unlock(&s->lock);
    pthread_mutex_lock(&s->mux_lock);
    ret = av_interleaved_write_frame(s->fmt, s->packet);
    pthread_mutex_unlock(&s->mux_lock);
    av_packet_unref(s->packet);
    if (ret < 0) {
      s->broken = 1;
      return -8;
    }
  }

  if (ret != AVERROR(EAGAIN) && ret != AVERROR_EOF) {
    s->broken = 1;
    return -9;
  }

  s->frames++;
  return 0;
}

static int encode_and_mux_audio_frame(NativeStream *s) {
  if (av_frame_make_writable(s->aframe) < 0)
    return -1;

  int ch = s->a_channels;
  int fs = s->a_frame_size;
  for (int c = 0; c < ch; c++) {
    float *dst = (float *)s->aframe->data[c];
    for (int i = 0; i < fs; i++)
      dst[i] = s->a_fifo[(i * ch) + c];
  }
  s->aframe->pts = s->a_pts;
  s->a_pts += fs;

  int ret = avcodec_send_frame(s->acodec, s->aframe);
  if (ret < 0)
    return -2;

  while ((ret = avcodec_receive_packet(s->acodec, s->apacket)) == 0) {
    av_packet_rescale_ts(s->apacket, s->acodec->time_base, s->astream->time_base);
    s->apacket->stream_index = s->astream->index;
    pthread_mutex_lock(&s->mux_lock);
    ret = av_interleaved_write_frame(s->fmt, s->apacket);
    pthread_mutex_unlock(&s->mux_lock);
    av_packet_unref(s->apacket);
    if (ret < 0)
      return -3;
  }
  if (ret != AVERROR(EAGAIN) && ret != AVERROR_EOF)
    return -4;

  int remaining = s->a_fifo_samples - fs;
  if (remaining > 0)
    memmove(s->a_fifo, s->a_fifo + (size_t)fs * ch, (size_t)remaining * ch * sizeof(float));
  s->a_fifo_samples = remaining;
  return 0;
}

__attribute__((visibility("default")))
int db_native_stream_push_audio(void *handle, const float *interleaved, int nb_samples) {
  NativeStream *s = (NativeStream *)handle;
  if (!s || s->broken || !s->has_audio || !interleaved || nb_samples <= 0)
    return -1;

  int ch = s->a_channels;
  int needed = s->a_fifo_samples + nb_samples;
  if (needed > s->a_fifo_cap_samples) {
    int cap = s->a_fifo_cap_samples > 0 ? s->a_fifo_cap_samples : s->a_frame_size * 4;
    while (cap < needed)
      cap *= 2;
    float *next = realloc(s->a_fifo, (size_t)cap * ch * sizeof(float));
    if (!next)
      return -2;
    s->a_fifo = next;
    s->a_fifo_cap_samples = cap;
  }

  memcpy(s->a_fifo + (size_t)s->a_fifo_samples * ch, interleaved, (size_t)nb_samples * ch * sizeof(float));
  s->a_fifo_samples += nb_samples;

  while (s->a_fifo_samples >= s->a_frame_size) {
    int ret = encode_and_mux_audio_frame(s);
    if (ret != 0) {
      s->broken = 1;
      return ret;
    }
  }
  return 0;
}

__attribute__((visibility("default")))
int db_native_stream_read(void *handle, uint8_t *dst, int dst_len, int64_t *read_pos, int *aligned,
                          int64_t min_keyframe_pos, int *keyframe_aligned, int *bytes_read) {
  NativeStream *s = (NativeStream *)handle;
  if (bytes_read)
    *bytes_read = 0;
  if (!s || !dst || dst_len <= 0 || !read_pos)
    return -1;

  pthread_mutex_lock(&s->lock);
  int64_t write_pos = s->write_pos;
  int64_t earliest = write_pos > s->ring_size ? write_pos - s->ring_size : 0;
  if (*read_pos < earliest) {
    pthread_mutex_unlock(&s->lock);
    return -2;
  }

  if (!aligned || *aligned == 0) {
    int64_t start = s->keyframe_pos >= min_keyframe_pos ? s->keyframe_pos : min_keyframe_pos;
    if (start < earliest)
      start = earliest;
    if (start > write_pos)
      start = write_pos;
    *read_pos = start;
    if (aligned)
      *aligned = 1;
    if (keyframe_aligned)
      *keyframe_aligned = s->keyframe_pos >= 0 && start == s->keyframe_pos;
  }

  int64_t available = write_pos - *read_pos;
  if (available <= 0) {
    pthread_mutex_unlock(&s->lock);
    return 0;
  }
  int to_copy = dst_len < available ? dst_len : (int)available;
  int offset = (int)(*read_pos % s->ring_size);
  int first = s->ring_size - offset;
  if (first > to_copy)
    first = to_copy;
  memcpy(dst, s->ring + offset, (size_t)first);
  if (to_copy > first)
    memcpy(dst + first, s->ring, (size_t)(to_copy - first));
  *read_pos += to_copy;
  if (bytes_read)
    *bytes_read = to_copy;
  pthread_mutex_unlock(&s->lock);
  return 0;
}

__attribute__((visibility("default")))
void db_native_stream_info(void *handle, DbLinuxStreamInfo *info) {
  if (!info)
    return;
  memset(info, 0, sizeof(*info));
  NativeStream *s = (NativeStream *)handle;
  if (!s) {
    info->broken = 1;
    info->keyframe_pos = -1;
    return;
  }
  pthread_mutex_lock(&s->lock);
  info->running = s->running;
  info->broken = s->broken;
  info->width = s->width;
  info->height = s->height;
  info->write_pos = s->write_pos;
  info->keyframe_pos = s->keyframe_pos;
  info->frames = s->frames;
  pthread_mutex_unlock(&s->lock);
}

__attribute__((visibility("default")))
void db_native_stream_destroy(void *handle) {
  NativeStream *s = (NativeStream *)handle;
  if (!s)
    return;
  s->running = 0;
  if (s->fmt)
    av_write_trailer(s->fmt);
  if (s->sws)
    sws_freeContext(s->sws);
  if (s->packet)
    av_packet_free(&s->packet);
  if (s->frame)
    av_frame_free(&s->frame);
  if (s->codec)
    avcodec_free_context(&s->codec);
  if (s->apacket)
    av_packet_free(&s->apacket);
  if (s->aframe)
    av_frame_free(&s->aframe);
  if (s->acodec)
    avcodec_free_context(&s->acodec);
  if (s->fmt) {
    if (s->fmt->pb)
      avio_context_free(&s->fmt->pb);
    avformat_free_context(s->fmt);
  }
  free(s->a_fifo);
  free(s->src_rows);
  free(s->ring);
  pthread_mutex_destroy(&s->mux_lock);
  pthread_mutex_destroy(&s->lock);
  free(s);
}

static NativeStream *lookup_stream(uint64_t stream_id) {
  if (stream_id == 0)
    return NULL;
  pthread_mutex_lock(&g_streams_lock);
  NativeStream *stream = g_streams[stream_id % 256];
  pthread_mutex_unlock(&g_streams_lock);
  return stream;
}

__attribute__((visibility("default")))
int db_linux_stream_start(uint32_t node_id, int fps, int bitrate_mbps, int max_res,
                          uint32_t adapter_vendor_id, const uint8_t *encoder_pref, uintptr_t encoder_pref_len,
                          const uint8_t *rtsp_url, uintptr_t rtsp_url_len,
                          int audio_enabled, int audio_sample_rate, int audio_channels,
                          uint64_t *out_stream_id) {
  (void)node_id;
  if (!out_stream_id)
    return -13;
  *out_stream_id = 0;
  if (ensure_ffmpeg_loaded() != 0)
    return -60;

  char *url = NULL;
  if (rtsp_url && rtsp_url_len > 0) {
    url = calloc(1, rtsp_url_len + 1);
    if (!url)
      return -14;
    memcpy(url, rtsp_url, rtsp_url_len);
  }

  NativeStream *stream = calloc(1, sizeof(NativeStream));
  free(url);
  if (!stream)
    return -50;
  pthread_mutex_init(&stream->lock, NULL);
  pthread_mutex_init(&stream->mux_lock, NULL);
  stream->fps = fps > 0 ? fps : 30;
  stream->bitrate_mbps = bitrate_mbps;
  stream->max_res = max_res;
  stream->adapter_vendor_id = adapter_vendor_id;
  stream->audio_enabled = audio_enabled ? 1 : 0;
  stream->a_sample_rate = audio_sample_rate > 0 ? audio_sample_rate : 48000;
  stream->a_channels = audio_channels > 0 ? audio_channels : 2;
  if (encoder_pref && encoder_pref_len > 0) {
    size_t len = encoder_pref_len < sizeof(stream->encoder_pref) - 1 ? (size_t)encoder_pref_len : sizeof(stream->encoder_pref) - 1;
    memcpy(stream->encoder_pref, encoder_pref, len);
    stream->encoder_pref[len] = '\0';
  } else {
    snprintf(stream->encoder_pref, sizeof(stream->encoder_pref), "auto");
  }
  stream->ring_size = RING_SIZE;
  stream->keyframe_pos = -1;
  stream->ring = malloc((size_t)stream->ring_size);
  if (!stream->ring) {
    db_native_stream_destroy(stream);
    return -50;
  }

  pthread_mutex_lock(&g_streams_lock);
  uint64_t stream_id = g_next_stream_id++;
  for (int attempts = 0; attempts < 256; attempts++, stream_id = g_next_stream_id++) {
    uint64_t slot = stream_id % 256;
    if (slot == 0)
      continue;
    if (!g_streams[slot]) {
      g_streams[slot] = stream;
      *out_stream_id = stream_id;
      pthread_mutex_unlock(&g_streams_lock);
      return 0;
    }
  }
  pthread_mutex_unlock(&g_streams_lock);
  db_native_stream_destroy(stream);
  return -51;
}

__attribute__((visibility("default")))
int db_linux_stream_push_frame(uint64_t stream_id, const DbLinuxFrame *frame) {
  NativeStream *stream = lookup_stream(stream_id);
  return stream ? db_native_stream_push_memfd(stream, frame) : -1;
}

__attribute__((visibility("default")))
int db_linux_stream_push_audio(uint64_t stream_id, const float *interleaved, int nb_samples) {
  NativeStream *stream = lookup_stream(stream_id);
  return stream ? db_native_stream_push_audio(stream, interleaved, nb_samples) : -1;
}

__attribute__((visibility("default")))
int db_linux_stream_read(uint64_t stream_id, uint8_t *dst, int dst_len, int64_t *read_pos,
                         int *aligned, int64_t min_keyframe_pos, int *keyframe_aligned, int *bytes_read) {
  NativeStream *stream = lookup_stream(stream_id);
  return stream ? db_native_stream_read(stream, dst, dst_len, read_pos, aligned, min_keyframe_pos, keyframe_aligned, bytes_read) : -1;
}

__attribute__((visibility("default")))
int db_linux_stream_info(uint64_t stream_id, DbLinuxStreamInfo *info) {
  NativeStream *stream = lookup_stream(stream_id);
  db_native_stream_info(stream, info);
  return stream ? 0 : -1;
}

__attribute__((visibility("default")))
void db_linux_stream_stop(uint64_t stream_id) {
  if (stream_id == 0)
    return;
  pthread_mutex_lock(&g_streams_lock);
  uint64_t slot = stream_id % 256;
  NativeStream *stream = g_streams[slot];
  g_streams[slot] = NULL;
  pthread_mutex_unlock(&g_streams_lock);
  if (stream)
    db_native_stream_destroy(stream);
}

__attribute__((visibility("default")))
const char *db_linux_stream_last_error(void) {
  return g_last_error;
}

__attribute__((visibility("default")))
int db_linux_vcam_open(int width, int height) {
  if (width <= 0 || height <= 0)
    return -1;

  for (int i = 0; i < 64; i++) {
    char path[32];
    snprintf(path, sizeof(path), "/dev/video%d", i);
    int fd = open(path, O_RDWR | O_NONBLOCK);
    if (fd < 0)
      continue;

    struct v4l2_capability cap;
    memset(&cap, 0, sizeof(cap));
    if (ioctl(fd, VIDIOC_QUERYCAP, &cap) == 0 &&
        strstr((const char *)cap.card, "DesktopBuddy") != NULL) {
      struct v4l2_format fmt;
      memset(&fmt, 0, sizeof(fmt));
      fmt.type = V4L2_BUF_TYPE_VIDEO_OUTPUT;
      fmt.fmt.pix.width = (uint32_t)width;
      fmt.fmt.pix.height = (uint32_t)height;
      fmt.fmt.pix.pixelformat = V4L2_PIX_FMT_BGR24;
      fmt.fmt.pix.field = V4L2_FIELD_NONE;
      fmt.fmt.pix.bytesperline = (uint32_t)(width * 3);
      fmt.fmt.pix.sizeimage = (uint32_t)(width * height * 3);
      fmt.fmt.pix.colorspace = V4L2_COLORSPACE_SRGB;
      if (ioctl(fd, VIDIOC_S_FMT, &fmt) == 0) {
        int flags = fcntl(fd, F_GETFL, 0);
        if (flags >= 0)
          fcntl(fd, F_SETFL, flags & ~O_NONBLOCK);
        return fd;
      }
    }
    close(fd);
  }

  set_last_error("v4l2 virtual camera device not found", "DesktopBuddy - Camera");
  return -1;
}

__attribute__((visibility("default")))
int db_linux_vcam_write(int fd, const uint8_t *data, int len) {
  if (fd < 0 || !data || len <= 0)
    return -1;
  ssize_t written = write(fd, data, (size_t)len);
  return (int)written;
}

__attribute__((visibility("default")))
void db_linux_vcam_close(int fd) {
  if (fd >= 0)
    close(fd);
}
