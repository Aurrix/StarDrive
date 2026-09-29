using System;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using System.Threading;
using SDUtils;
using Ship_Game.Audio;
using Ship_Game.GameScreens.MainMenu;
using Ship_Game.GameScreens.NewGame;
using Vector2 = SDGraphics.Vector2;
using Rectangle = SDGraphics.Rectangle;
using Ship_Game.Universe;

namespace Ship_Game
{
    public sealed class CreatingNewGameScreen : GameScreen
    {
        readonly MainMenuScreen MainMenu;
        Texture2D LoadingScreenTexture;
        string AdviceText;

        readonly UniverseParams P;
        readonly UniverseGenerator Generator;
        TaskResult<UniverseScreen> BackgroundTask;
        bool UniverseInitialized, BordersReady;

        public CreatingNewGameScreen(MainMenuScreen menu, UniverseParams p)
            : base(null, toPause: null)
        {
            CanEscapeFromScreen = false;
            MainMenu = menu;
            P = p;
            Generator = new UniverseGenerator(p);
        }

        public override void LoadContent()
        {
            Log.LogEventStats(Log.GameEvent.NewGame, P);
            ScreenManager.ClearScene();
            LoadingScreenTexture = ResourceManager.LoadRandomLoadingScreen(Generator.Random, TransientContent);
            AdviceText = Fonts.Arial12Bold.ParseText(ResourceManager.LoadRandomAdvice(Generator.Random), 500f);

            BackgroundTask = Generator.GenerateAsync();
            base.LoadContent();
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;
            Mem.Dispose(ref LoadingScreenTexture);
            Mem.Dispose(ref BackgroundTask);
            base.Dispose(disposing);
        }

        public override bool HandleInput(InputState input)
        {
            if (!BordersReady || BackgroundTask?.IsComplete != true || !input.InGameSelect)
                return false;

            UniverseScreen us = BackgroundTask.Result ?? throw new NullReferenceException("CreatingNewGameScreen background task returned null", BackgroundTask.Error);
            GameAudio.StopGenericMusic(fadeout: true);
            ScreenManager.AddScreenNoLoad(us);

            ScreenManager.StopMusic();
            ScreenManager.RemoveScreen(MainMenu);

            ExitScreen();
            return true;
        }

        public override void Draw(SpriteBatch batch, DrawTimes elapsed)
        {
            ScreenManager.ClearScreen(Color.Black);

            if (BackgroundTask?.IsComplete == false)
            {
                // heavily throttle main draw thread, so the worker thread can turbo
                Thread.Sleep(33);
                if (IsDisposed) // just in case we tried to ALT+F4 during loading
                    return;
            }

            if (!GameBase.Base.IsDeviceGood) return;
            if (BackgroundTask?.IsComplete == true && !BordersReady)
            {
                UniverseScreen us = BackgroundTask.Result ?? throw new NullReferenceException(
                    "CreatingNewGameScreen background task returned null", BackgroundTask.Error);
                if (!UniverseInitialized)
                {
                    InitializeGeneratedUniverse(us);
                    UniverseInitialized = true;
                }
                BordersReady = us.PrepareLoadedBorderVisuals(batch.GraphicsDevice);
            }

            if (!batch.SafeBegin()) return;
            int width = ScreenWidth;
            int height = ScreenHeight;
            if (LoadingScreenTexture != null)
                batch.Draw(LoadingScreenTexture, new Rectangle(width / 2 - 960, height / 2 - 540, 1920, 1080), Color.White);

            var r = new Rectangle(width / 2 - 150, height - 25, 300, 25);
            float progress = BordersReady ? 1f : Math.Min(.99f,Generator.Progress.Percent);
            new ProgressBar(r) { Max = 100f, Progress = progress * 100f }.Draw(batch);

            var position = new Vector2(ScreenCenter.X - 250f, (float)(r.Y - Fonts.Arial12Bold.MeasureString(AdviceText).Y - 5.0));
            batch.DrawString(Fonts.Arial12Bold, AdviceText, position, Color.White);

            if (BackgroundTask?.IsComplete == true)
            {
                position.Y = (float)(position.Y - Fonts.Pirulen16.LineSpacing - 10.0);
                string token = BordersReady ? Localizer.Token(GameText.ClickToContinue) : BackgroundTask.Result.BorderLoadStatus;
                position.X = ScreenCenter.X - Fonts.Pirulen16.MeasureString(token).X / 2f;

                batch.DrawString(Fonts.Pirulen16, token, position, CurrentFlashColor);
            }

            batch.SafeEnd();
        }

        internal static void InitializeGeneratedUniverse(UniverseScreen universe)
        {
            // Initialize while the loading screen still owns drawing. The sim
            // thread remains waiting for the universe's first DrawCompleted event.
            universe.InvokeLoadContent();
            universe.UState.Objects.Update(new FixedSimTime(0.01f));
        }
    }
}
