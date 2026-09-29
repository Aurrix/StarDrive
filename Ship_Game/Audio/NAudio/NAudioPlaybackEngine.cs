using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SDUtils;
using System;

#nullable enable

namespace Ship_Game.Audio.NAudio;

internal class NAudioPlaybackEngine : IDisposable
{
    public static readonly int SampleRate = 44100;
    public static readonly int Channels = 2;

    readonly IWavePlayer OutputDevice;
    readonly NAudioSampleMixer Mixer;
    readonly IWavePlayer MusicOutput;
    readonly NAudioSampleMixer MusicMixer;

    // pre-sampled cache for Weapon and Warp effects
    readonly Map<string, CachedSoundEffect> SfxCache = new();

    public WaveFormat WaveFormat { get; }

    public NAudioPlaybackEngine(MMDevice device)
        : this(latency => new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: latency))
    {
    }

    internal NAudioPlaybackEngine(Func<int, IWavePlayer> createOutput)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        Mixer = new(WaveFormat) { ReadFully = true };
        MusicMixer = new(WaveFormat) { ReadFully = true };
        OutputDevice = createOutput(50);
        try
        {
            // Independent WASAPI playback threads: expensive SFX mixing cannot
            // block music reads. The native music buffer tolerates short managed
            // stalls without imposing the same latency on UI and combat sounds.
            MusicOutput = createOutput(300);
            OutputDevice.Init(Mixer);
            MusicOutput.Init(MusicMixer);
            OutputDevice.Play();
            MusicOutput.Play();
        }
        catch
        {
            MusicOutput?.Dispose();
            OutputDevice.Dispose();
            MusicMixer.Dispose();
            Mixer.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        MusicOutput.Dispose();
        OutputDevice.Dispose(); // automatically calls Stop()
        // Join playback before disposing providers they could still be reading.
        MusicMixer.Dispose();
        Mixer.Dispose();
    }

    /// <summary>
    /// Global volume of the OutputDevice. WARNING: WasapiOut shares the per-process Windows
    /// audio session, so setting this also affects MediaFoundation (XNA VideoPlayer) audio.
    /// For NAudio-only mute that leaves video audio audible, use <see cref="MixerMasterVolume"/>.
    /// </summary>
    public float Volume
    {
        get => OutputDevice.Volume;
        set => OutputDevice.Volume = value;
    }

    /// <summary>
    /// Master volume applied to the NAudio mixer's output before it reaches the WasapiOut
    /// device. Use this to silence in-game music/SFX without affecting MediaFoundation video.
    /// Clamped to [0, 1] so a caller mistake can't polarity-invert or NaN-poison the buffer.
    /// </summary>
    public float MixerMasterVolume
    {
        get => Mixer.MasterVolume;
        set => MusicMixer.MasterVolume = Mixer.MasterVolume = float.IsNaN(value) ? 1f : Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Adds a new sound to the mixer
    /// The sound is automatically removed when it finishes playing
    /// </summary>
    public IAudioInstance? Play(AudioCategory category, AudioEmitter? emitter, string audioFile, float volume)
    {
        try
        {
            float? effectiveVolume = emitter?.GetEffectiveVolume(category, volume);
            if (effectiveVolume < 0.0001f)
                return null; // this sound can't be heard anyway, ignore it

            ISampleProvider provider;

            if (category.MemoryCache)
            {
                CachedSoundEffect? cached;
                lock (SfxCache)
                {
                    SfxCache.TryGetValue(audioFile, out cached);
                }
                if (cached == null)
                {
                    // generating the cache will be sloooow, even on a very fast system it can take 300ms+
                    //PerfTimer t = new();
                    cached = new(WaveFormat, audioFile);
                    //double elapsedMs = t.ElapsedMillis;
                    //Log.Write(ConsoleColor.Green, $"Caching {audioFile} elapsed:{elapsedMs:0.1}ms");

                    lock (SfxCache)
                        SfxCache.Add(audioFile, cached);
                }
                provider = cached.CreateReader();
            }
            else
            {
                provider = new NAudioFileReader(WaveFormat, audioFile);
            }

            //Log.Write(ConsoleColor.Green, $"Start {audioFile} volume={volume}");
            NAudioSampleInstance instance = new(category, emitter, provider, volume);
            NAudioSampleMixer mixer = category.Name.IndexOf("Music", StringComparison.OrdinalIgnoreCase) >= 0
                ? MusicMixer : Mixer;
            mixer.AddMixerInput(instance);
            return instance;
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to play audio file: {ex}");
            return null;
        }
    }
}
