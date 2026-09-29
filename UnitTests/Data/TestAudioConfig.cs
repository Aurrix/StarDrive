using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;
using Ship_Game.Audio;
using Ship_Game;
using Ship_Game.Audio.NAudio;

namespace UnitTests.Data
{
    [TestClass]
    public class TestAudioConfig : StarDriveTest
    {
        sealed class TestOutput : IWavePlayer
        {
            public IWaveProvider Provider;
            public WaveFormat OutputWaveFormat => Provider?.WaveFormat;
            public bool Disposed;
            public float Volume { get; set; } = 1;
            public PlaybackState PlaybackState { get; private set; }
            public event EventHandler<StoppedEventArgs> PlaybackStopped;
            public void Init(IWaveProvider provider) => Provider = provider;
            public void Play() => PlaybackState = PlaybackState.Playing;
            public void Pause() => PlaybackState = PlaybackState.Paused;
            public void Stop()
            {
                PlaybackState = PlaybackState.Stopped;
                PlaybackStopped?.Invoke(this,new StoppedEventArgs());
            }
            public void Dispose() { Stop(); Disposed = true; }
            public bool ReadAudible()
            {
                var bytes = new byte[44100 * 2 * sizeof(float)];
                int count = Provider.Read(bytes,0,bytes.Length);
                for (int i = 0; i < count; i += sizeof(float))
                    if (Math.Abs(BitConverter.ToSingle(bytes,i)) > 0.00001f) return true;
                return false;
            }
        }

        [TestMethod]
        public void MusicUsesIndependentBufferedOutputAndVideoMute()
        {
            var effects = new TestOutput();
            var music = new TestOutput();
            using var config = new AudioConfig();
            using (var engine = new NAudioPlaybackEngine(latency => latency switch
            {
                50 => effects,
                300 => music,
                _ => throw new AssertFailedException("Unexpected output latency")
            }))
            {
                string file = GetAudioPath("Music/AmbientMusic.0.m4a").FullName;
                using var track = engine.Play(config.GetCategory("Music"),null,file,1);
                Assert.IsNotNull(track);
                Assert.IsTrue(music.ReadAudible(),"Music must reach its dedicated output");
                Assert.IsFalse(effects.ReadAudible(),"Music leaked into the effects mixer");
                using var effect = engine.Play(config.GetCategory("Weapons"),null,
                    GetAudioPath("UI/sd_ui_notification_research_01.m4a").FullName,1);
                Assert.IsNotNull(effect);
                Assert.IsTrue(effects.ReadAudible());
                engine.MixerMasterVolume = 0;
                Assert.IsFalse(music.ReadAudible(),"Video mute must silence both outputs");
                Assert.IsFalse(effects.ReadAudible());
                engine.MixerMasterVolume = 1;
                Assert.IsTrue(music.ReadAudible(),"Music must resume after video mute");
            }
            Assert.IsTrue(music.Disposed && effects.Disposed);
        }

        [TestMethod]
        public void MusicOutputInitializationFailureReleasesEffectsOutput()
        {
            var effects = new TestOutput();
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                new NAudioPlaybackEngine(latency => latency == 50 ? effects
                    : throw new InvalidOperationException("Device unavailable")));
            Assert.IsTrue(effects.Disposed);
        }

        static bool IsSupportedFileExtension(string fileName)
        {
            return fileName.EndsWith(".m4a")
                || fileName.EndsWith(".aac")
                || fileName.EndsWith(".mp4")
                || fileName.EndsWith(".mp3")
                || fileName.EndsWith(".wav");
        }

        [TestMethod]
        public void CanParseMultipleSoundCategories()
        {
            AudioConfig config = new();
            AssertEqual(8, config.Categories.Length);
            SoundEffect ambient = config.GetSoundEffect("AmbientMusic");
            AssertEqual("Beyond the Frontier", ambient.GetTrackTitle("Music/AmbientMusic.0.m4a"));
            AssertEqual("Humble Beginnings", ambient.GetTrackTitle("Music/sd2-1.m4a"));
            AssertEqual("My Mod Track", ambient.GetTrackTitle("Music/My_Mod_Track.m4a"));
            AssertEqual("Jeff Dodson", ambient.GetTrackArtist("Music/AmbientMusic.0.m4a"));
            AssertEqual("Marius Masalar", ambient.GetTrackArtist("Music/sd2-1.m4a"));
            AssertEqual("Gustav Holst", ambient.GetTrackArtist("Music/sd2-3.m4a"));
            AssertEqual("Unknown artist", ambient.GetTrackArtist("Music/My_Mod_Track.m4a"));
            foreach (string track in ambient.Sounds)
                AssertTrue(ambient.TrackTitles.ContainsKey(track), $"Missing display title for {track}");
            foreach (AudioCategory category in config.Categories)
            {
                AssertTrue(category.Name.NotEmpty(), "Category name cannot be empty");
                AssertGreaterThan(category.Volume, 0.01f, "Expected default volume to be set");
                AssertGreaterThan(category.SoundEffects.Length, 1, "Expected more than one SoundEffects");
                foreach (SoundEffect effect in category.SoundEffects)
                {
                    AssertTrue(effect.Id.NotEmpty(), "Effect Id cannot be empty");
                    AssertGreaterThan(effect.Volume, 0.01f, $"Expected effect={effect.Id} volume to be set");
                    if (effect.Sound.NotEmpty())
                    {
                        AssertTrue(IsSupportedFileExtension(effect.Sound), $"Effect={effect.Id} unsupported sound={effect.Sound}");
                    }
                    else if (effect.Sounds is { Length: > 0 })
                    {
                        foreach (string sound in effect.Sounds)
                            AssertTrue(IsSupportedFileExtension(sound), $"Effect={effect.Id} unsupported sound={sound}");
                    }
                    else
                    {
                        throw new AssertFailedException($"Expected effect={effect.Id} to have Sound or Sounds properties");
                    }
                }
            }
        }

        static FileInfo GetAudioPath(string soundPath)
        {
            string relPath = "Audio/" + soundPath;
            FileInfo fullPath = ResourceManager.GetModOrVanillaFile(relPath);
            if (fullPath is not { Exists: true })
                throw new FileNotFoundException($"Sound file does not exist: {relPath}");
            return fullPath;
        }

        /// <summary>
        /// This is an interesting unit test approach for the main release build.
        /// It ensures that all audio files referenced in AudioConfig actually exist before installer is packaged.
        /// </summary>
        [TestMethod]
        public void EnsureAllAudioFilesExist()
        {
            AudioConfig config = new();
            foreach (AudioCategory category in config.Categories)
            {
                foreach (SoundEffect effect in category.SoundEffects)
                {
                    if (effect.Sound.NotEmpty())
                        GetAudioPath(effect.Sound);
                    else if (effect.Sounds is { Length: > 0 })
                        foreach (string sound in effect.Sounds)
                            GetAudioPath(sound);
                }
            }
        }

        [TestMethod]
        public void PerEffectMaxConcurrentOverridesCategoryLimit()
        {
            AudioConfig config = new();
            AudioCategory weapons = config.GetCategory("Weapons");

            SoundEffect capped = config.GetSoundEffect("sd_weapon_rocket_flight_01");
            AssertGreaterThan(capped.MaxConcurrent, 0, "Expected a per-effect cap on the flight cue");
            AssertGreaterThan(weapons.MaxConcurrentSoundsPerEffect, capped.MaxConcurrent,
                "This test only means something while the per-effect cap is the lower of the two");

            capped.NumActiveInstances = capped.MaxConcurrent - 1;
            AssertTrue(weapons.CanPlayEffect(capped), "Expected to play below the effect cap");
            capped.NumActiveInstances = capped.MaxConcurrent;
            AssertFalse(weapons.CanPlayEffect(capped), "Expected the effect cap to block, not the category cap");
            capped.NumActiveInstances = 0;

            SoundEffect uncapped = config.GetSoundEffect("sd_weapon_rocket_explode_01");
            AssertEqual(0, uncapped.MaxConcurrent);
            uncapped.NumActiveInstances = weapons.MaxConcurrentSoundsPerEffect - 1;
            AssertTrue(weapons.CanPlayEffect(uncapped), "Expected the category cap to still apply");
            uncapped.NumActiveInstances = weapons.MaxConcurrentSoundsPerEffect;
            AssertFalse(weapons.CanPlayEffect(uncapped), "Expected the category cap to block");
            uncapped.NumActiveInstances = 0;
        }

        [TestMethod]
        public void TroopTakeOffAndLandingSoundsAreTurnedDownAndDoNotStack()
        {
            AudioConfig config = new();
            AudioCategory ground = config.GetCategory("Ground");
            foreach (string id in new[] { "sd_troop_takeoff", "sd_troop_land" })
            {
                SoundEffect effect = config.GetSoundEffect(id);
                AssertEqual(0.001f, 0.4f, effect.Volume, $"{id} plays with no distance falloff, so it is turned down");

                effect.NumActiveInstances = 1;
                AssertTrue(ground.CanPlayEffect(effect), $"{id}: a second quick launch still plays");
                effect.NumActiveInstances = 2;
                AssertFalse(ground.CanPlayEffect(effect), $"{id}: quick launches stop stacking at two");
                effect.NumActiveInstances = 0;
            }
        }

        [TestMethod]
        public void CanCacheAudioData()
        {
            FileInfo fullPath = GetAudioPath("UI/sd_ui_notification_research_01.m4a");
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            CachedSoundEffect cached = new(format, fullPath.FullName);
            AssertEqual(292864, cached.NumSamples);

            // test that we can read the cached data
            // create an inconveniently sized buffer to guarantee multiple cross-chunk reads
            float[] buffer1 = new float[(int)(format.SampleRate * format.Channels * 0.66f)];
            ISampleProvider reader1 = cached.CreateReader();
            int totalSamples1 = 0;
            for (int n; (n = reader1.Read(buffer1, 0, buffer1.Length)) > 0; totalSamples1 += n) {}
            AssertEqual(292864, totalSamples1);

            // read again, but this time with a much bigger buffer
            float[] buffer2 = new float[(int)(format.SampleRate * format.Channels * 2.66f)];
            ISampleProvider reader2 = cached.CreateReader();
            int totalSamples2 = 0;
            for (int n; (n = reader2.Read(buffer2, 0, buffer2.Length)) > 0; totalSamples2 += n) {}
            AssertEqual(292864, totalSamples2);
        }
    }
}
