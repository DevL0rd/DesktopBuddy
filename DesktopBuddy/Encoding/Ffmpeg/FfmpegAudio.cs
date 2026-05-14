using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using ResoniteModLoader;

namespace DesktopBuddy;

public sealed unsafe partial class FfmpegEncoder
{

    private void SetupAudioStream()
    {
        var audioCodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (audioCodec == null) { Log.Msg($"[FfmpegEnc:{_streamId}] AAC encoder not found, audio disabled"); return; }

        _audioCodecCtx = ffmpeg.avcodec_alloc_context3(audioCodec);
        _audioCodecCtx->sample_rate = 48000;
        _audioCodecCtx->ch_layout = new AVChannelLayout { order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE, nb_channels = 2, u = new AVChannelLayout_u { mask = ffmpeg.AV_CH_LAYOUT_STEREO } };
        _audioCodecCtx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioCodecCtx->bit_rate = 128000;
        _audioCodecCtx->time_base = new AVRational { num = 1, den = 48000 };
        _audioCodecCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        int ret = ffmpeg.avcodec_open2(_audioCodecCtx, audioCodec, null);
        if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] Audio codec open failed: {FfmpegError(ret)}"); return; }

        _audioStream = ffmpeg.avformat_new_stream(_fmtCtx, null);
        ffmpeg.avcodec_parameters_from_context(_audioStream->codecpar, _audioCodecCtx);
        _audioStream->time_base = _audioCodecCtx->time_base;

        _audioFrame = ffmpeg.av_frame_alloc();
        _audioFrame->nb_samples = _audioCodecCtx->frame_size;
        _audioFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _audioFrame->ch_layout = _audioCodecCtx->ch_layout;
        _audioFrame->sample_rate = 48000;
        ffmpeg.av_frame_get_buffer(_audioFrame, 0);

        _audioScratch = new float[48000 * 2];
        _audioPkt = ffmpeg.av_packet_alloc();

        Log.Msg($"[FfmpegEnc:{_streamId}] Audio stream added: AAC 48kHz stereo 128kbps");
    }

    private void StartAudioEncodeThreadIfReady()
    {
        if (_audioCodecCtx == null || _audioFrame == null || _audioPkt == null || _audioCapture == null || !_audioCapture.IsCapturing)
            return;
        if (_audioEncodeThread != null)
            return;

        _audioEncodeThread = new Thread(AudioEncodeLoop)
        { Name = $"FfmpegEnc:{_streamId}:Audio", IsBackground = true };
        _audioEncodeThread.Start();
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode thread launched after muxer header");
    }


    private static int GetStreamFps()
    {
        int configured = DesktopBuddyMod.RuntimeStreamFps;
        return Math.Clamp(configured, 1, 240);
    }

    private void AudioEncodeLoop()
    {
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode thread started");
        while (!_disposed)
        {
            Thread.Sleep(33);
            if (_disposed) break;
            try { EncodeAudio(); }
            catch (Exception ex) { if (!_disposed) Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode error: {ex.Message}"); }
        }
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio encode thread stopped");
    }

    private void EncodeAudio()
    {
        if (_audioScratch == null || _audioFrame == null || _audioPkt == null) return;

        int frameSize = _audioCodecCtx->frame_size;
        int channels = 2;
        int samplesPerFrame = frameSize * channels;
        int maxReadSamples = Math.Min(_audioScratch.Length, samplesPerFrame * 4);
        int targetLiveSamples = samplesPerFrame * 4;
        int maxLiveSamples = samplesPerFrame * 12;

        long writePos = _audioCapture.WritePosition;
        long available = writePos - _audioReadPos;
        if (available <= 0) return;

        if (available > maxLiveSamples)
        {
            long dropped = available - targetLiveSamples;
            _audioReadPos = writePos - targetLiveSamples;
            _audioReadPos -= _audioReadPos % samplesPerFrame;
            available = writePos - _audioReadPos;
            RecordAudioLiveCatchup(dropped, available, channels);
        }

        int read = _audioCapture.ReadSamples(_audioScratch, maxReadSamples, ref _audioReadPos);
        if (read <= 0) return;

        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsedSec = _startTicks != 0
            ? (double)(nowTicks - _startTicks) / System.Diagnostics.Stopwatch.Frequency
            : 0.0;
        long liveAudioPts = Math.Max(0, (long)(elapsedSec * 48000));
        long firstFramePts = Math.Max(0, liveAudioPts - (read / channels));

        int offset = 0;
        while (offset + samplesPerFrame <= read)
        {
            ffmpeg.av_frame_make_writable(_audioFrame);
            _audioFrame->nb_samples = frameSize;

            float* left = (float*)_audioFrame->data[0];
            float* right = (float*)_audioFrame->data[1];
            fixed (float* src = &_audioScratch[offset])
            {
                for (int i = 0; i < frameSize; i++)
                {
                    left[i] = src[i * channels];
                    right[i] = src[i * channels + 1];
                }
            }

            long audioPts = firstFramePts + (offset / channels);
            if (audioPts <= _lastAudioPts)
                audioPts = _lastAudioPts + frameSize;
            _audioFrame->pts = audioPts;
            _lastAudioPts = audioPts;
            _audioSamplesEncoded += frameSize;

            int ret = ffmpeg.avcodec_send_frame(_audioCodecCtx, _audioFrame);
            if (ret < 0) { Log.Msg($"[FfmpegEnc:{_streamId}] Audio send_frame failed: {FfmpegError(ret)}"); break; }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_packet(_audioCodecCtx, _audioPkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
                if (ret < 0) break;

                _audioPkt->stream_index = _audioStream->index;
                ffmpeg.av_packet_rescale_ts(_audioPkt, _audioCodecCtx->time_base, _audioStream->time_base);
                lock (_muxerLock)
                {
                    if (!_rtspBroken)
                    {
                        ret = ffmpeg.av_interleaved_write_frame(_fmtCtx, _audioPkt);
                        if (ret < 0)
                        {
                            Log.Msg($"[FfmpegEnc:{_streamId}] av_interleaved_write_frame (audio) failed: {FfmpegError(ret)}");
                            if (_rtspUrl != null) _rtspBroken = true;
                        }
                    }
                }
                ffmpeg.av_packet_unref(_audioPkt);
            }

            offset += samplesPerFrame;
        }

        int unconsumed = read - offset;
        if (unconsumed > 0)
            _audioReadPos -= unconsumed;
    }

    private void RecordAudioLiveCatchup(long droppedSamples, long remainingSamples, int channels)
    {
        long events = Interlocked.Increment(ref _audioLiveCatchupEvents);
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long lastTicks = Interlocked.Read(ref _audioLastCatchupLogTicks);
        bool shouldLog = events == 1 ||
            lastTicks == 0 ||
            (double)(nowTicks - lastTicks) / System.Diagnostics.Stopwatch.Frequency >= 30.0;

        if (!shouldLog)
            return;

        Interlocked.Exchange(ref _audioLastCatchupLogTicks, nowTicks);
        Log.Msg($"[FfmpegEnc:{_streamId}] Audio live catch-up summary: dropped {droppedSamples / (double)(48000 * channels):F3}s, remaining {remainingSamples / (double)(48000 * channels):F3}s, events={events}");
    }

}
