using System;
using System.Runtime.InteropServices;
using Awwdio;
using Elements.Assets;
using FrooxEngine;

namespace DesktopBuddy;

public class DesktopAudioSource : Component, IWorldAudioDataSource
{
    private AudioCapture _audioCapture;
    private long _readPos;

    public bool IsActive => Enabled && _audioCapture != null;
    public int ChannelCount => 2;

    public void SetAudioCapture(AudioCapture capture)
    {
        _audioCapture = capture;
        _readPos = capture?.WritePosition ?? 0;
        Log.Msg($"[DesktopAudioSource] AudioCapture set, readPos synced to {_readPos}");
    }

    public void Read<S>(Span<S> buffer, AudioSimulator system) where S : unmanaged, IAudioSample<S>
    {
        var audio = _audioCapture;
        if (audio == null)
        {
            buffer.Clear();
            return;
        }

        int framesNeeded = buffer.Length;
        int floatsNeeded = framesNeeded * 2;

        if (floatsNeeded <= 2048)
        {
            Span<float> scratch = stackalloc float[floatsNeeded];
            int read = audio.ReadSamples(scratch, ref _readPos);
            FillBuffer(buffer, scratch, read);
        }
        else
        {
            var scratch = new float[floatsNeeded];
            int read = audio.ReadSamples(scratch, floatsNeeded, ref _readPos);
            FillBuffer(buffer, scratch.AsSpan(0, read > 0 ? read : 0), read);
        }
    }

    private static void FillBuffer<S>(Span<S> buffer, Span<float> samples, int readCount) where S : unmanaged, IAudioSample<S>
    {
        int frames = readCount / 2;

        if (typeof(S) == typeof(StereoSample))
        {
            var stereo = MemoryMarshal.Cast<S, StereoSample>(buffer);
            for (int i = 0; i < frames && i < stereo.Length; i++)
                stereo[i] = new StereoSample(samples[i * 2], samples[i * 2 + 1]);
            for (int i = frames; i < stereo.Length; i++)
                stereo[i] = default;
        }
        else if (typeof(S) == typeof(MonoSample))
        {
            var mono = MemoryMarshal.Cast<S, MonoSample>(buffer);
            for (int i = 0; i < frames && i < mono.Length; i++)
                mono[i] = new MonoSample((samples[i * 2] + samples[i * 2 + 1]) * 0.5f);
            for (int i = frames; i < mono.Length; i++)
                mono[i] = default;
        }
        else
        {
            buffer.Clear();
        }
    }
}
