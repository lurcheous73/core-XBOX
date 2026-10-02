using BrimstoneXbox.Services;
using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace BrimstoneXbox
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;

            EnteredBackground += App_EnteredBackground;
            LeavingBackground += App_LeavingBackground;
            Suspending += App_Suspending;
        }

        void App_EnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            PlaybackService.Instance.SetBackgroundMode(true);

            // The Xbox background memory budget is tighter than foreground.
            // Release unused managed objects, but deliberately keep the singleton
            // MediaPlayer and its active source alive.
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Optimized,
                false);
        }

        void App_LeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            PlaybackService.Instance.SetBackgroundMode(false);
        }

        void App_Suspending(object sender, SuspendingEventArgs e)
        {
            PlaybackService.Instance.SaveState();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null)
            {
                frame.Navigate(typeof(MainPage), e.Arguments);
            }

            Window.Current.Activate();
        }
    }
}
