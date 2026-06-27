#include <dlfcn.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/syscall.h>
#include <unistd.h>

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

typedef int (*start_fn)(uint32_t, int, int, int, uint32_t, const uint8_t *, uintptr_t, const uint8_t *, uintptr_t, uint64_t *);
typedef int (*push_fn)(uint64_t, const DbLinuxFrame *);
typedef int (*read_fn)(uint64_t, uint8_t *, int, int64_t *, int *, int64_t, int *, int *);
typedef int (*info_fn)(uint64_t, DbLinuxStreamInfo *);
typedef const char *(*error_fn)(void);
typedef void (*stop_fn)(uint64_t);

static int create_memfd(const char *name) {
#ifdef SYS_memfd_create
  return (int)syscall(SYS_memfd_create, name, 0);
#else
  (void)name;
  return -1;
#endif
}

static int write_test_frame(int fd, int width, int height, int stride, int frame_index) {
  uint8_t *row = malloc((size_t)stride);
  if (!row)
    return -1;

  for (int y = 0; y < height; y++) {
    memset(row, 0, (size_t)stride);
    for (int x = 0; x < width; x++) {
      uint8_t *px = row + x * 4;
      px[0] = (uint8_t)((x + frame_index * 7) & 0xff);
      px[1] = (uint8_t)((y + frame_index * 5) & 0xff);
      px[2] = (uint8_t)((x + y + frame_index * 11) & 0xff);
      px[3] = 0xff;
    }
    if (pwrite(fd, row, (size_t)stride, (off_t)y * stride) != stride) {
      free(row);
      return -1;
    }
  }

  free(row);
  return 0;
}

static int has_ts_sync(const uint8_t *buffer, int length) {
  if (!buffer || length < 188 || buffer[0] != 0x47)
    return 0;
  int packets = length / 188;
  if (packets > 16)
    packets = 16;
  for (int i = 1; i < packets; i++) {
    if (buffer[i * 188] != 0x47)
      return 0;
  }
  return 1;
}

int main(int argc, char **argv) {
  const char *path = argc > 1 ? argv[1] : "./libdesktopbuddy_linux_stream.so";
  uint32_t vendor_id = argc > 2 ? (uint32_t)strtoul(argv[2], NULL, 0) : 0x10DE;
  const char *encoder_pref = argc > 3 ? argv[3] : "auto";
  void *module = dlopen(path, RTLD_NOW | RTLD_LOCAL);
  if (!module) {
    fprintf(stderr, "dlopen stream module failed: %s\n", dlerror());
    return 2;
  }

  start_fn start = (start_fn)dlsym(module, "db_linux_stream_start");
  push_fn push = (push_fn)dlsym(module, "db_linux_stream_push_frame");
  read_fn read_stream = (read_fn)dlsym(module, "db_linux_stream_read");
  info_fn info = (info_fn)dlsym(module, "db_linux_stream_info");
  error_fn last_error = (error_fn)dlsym(module, "db_linux_stream_last_error");
  stop_fn stop = (stop_fn)dlsym(module, "db_linux_stream_stop");
  if (!start || !push || !read_stream || !info || !last_error || !stop) {
    fprintf(stderr, "missing stream exports: %s\n", dlerror());
    return 3;
  }

  uint64_t stream_id = 0;
  int status = start(1, 30, 8, 1280, vendor_id, (const uint8_t *)encoder_pref, strlen(encoder_pref), NULL, 0, &stream_id);
  printf("start status=%d id=%llu vendor=0x%x encoder=%s error=%s\n",
         status,
         (unsigned long long)stream_id,
         vendor_id,
         encoder_pref,
         last_error() ? last_error() : "");
  if (status != 0 || stream_id == 0) {
    dlclose(module);
    return 1;
  }

  const int width = 320;
  const int height = 180;
  const int stride = width * 4;
  int fd = create_memfd("desktopbuddy-stream-harness");
  if (fd < 0) {
    perror("memfd_create");
    stop(stream_id);
    dlclose(module);
    return 4;
  }
  if (ftruncate(fd, (off_t)stride * height) != 0) {
    perror("ftruncate");
    close(fd);
    stop(stream_id);
    dlclose(module);
    return 5;
  }

  uint8_t read_buffer[256 * 1024];
  int64_t read_pos = 0;
  int aligned = 0;
  int keyframe_aligned = 0;
  int total_read = 0;
  int saw_ts_sync = 0;
  DbLinuxFrame frame = {
      .fd = fd,
      .width = width,
      .height = height,
      .stride = stride,
  };

  for (int i = 0; i < 60; i++) {
    if (write_test_frame(fd, width, height, stride, i) != 0) {
      fprintf(stderr, "failed to write synthetic frame\n");
      close(fd);
      stop(stream_id);
      dlclose(module);
      return 6;
    }

    status = push(stream_id, &frame);
    if (status != 0) {
      fprintf(stderr, "push failed frame=%d status=%d error=%s\n", i, status, last_error() ? last_error() : "");
      close(fd);
      stop(stream_id);
      dlclose(module);
      return 7;
    }

    int bytes_read = 0;
    status = read_stream(stream_id, read_buffer, sizeof(read_buffer), &read_pos, &aligned, 0, &keyframe_aligned, &bytes_read);
    if (status != 0) {
      fprintf(stderr, "read failed frame=%d status=%d\n", i, status);
      close(fd);
      stop(stream_id);
      dlclose(module);
      return 8;
    }
    if (bytes_read > 0) {
      total_read += bytes_read;
      if (!saw_ts_sync && has_ts_sync(read_buffer, bytes_read))
        saw_ts_sync = 1;
    }
  }

  DbLinuxStreamInfo stream_info;
  memset(&stream_info, 0, sizeof(stream_info));
  info(stream_id, &stream_info);
  printf("info running=%d broken=%d size=%dx%d frames=%lld write=%lld keyframe=%lld read=%d ts_sync=%d\n",
         stream_info.running,
         stream_info.broken,
         stream_info.width,
         stream_info.height,
         (long long)stream_info.frames,
         (long long)stream_info.write_pos,
         (long long)stream_info.keyframe_pos,
         total_read,
         saw_ts_sync);

  close(fd);
  stop(stream_id);
  dlclose(module);
  return stream_info.running != 0 &&
                 stream_info.broken == 0 &&
                 stream_info.frames >= 60 &&
                 stream_info.write_pos > 0 &&
                 stream_info.keyframe_pos >= 0 &&
                 total_read > 0 &&
                 saw_ts_sync
             ? 0
             : 9;
}
