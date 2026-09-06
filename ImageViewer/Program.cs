using Avalonia;
using System;

/*

This is a test app that tests the ImageFormats class library
included with this project. Refer to the individual source code
files for each image type for more information.

Copyright 2013+ Dmitry Brant
https://dmitrybrant.com

License: MIT

*/

namespace ImageViewer
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application. Don't use any Avalonia types before
        /// AppMain is called; the framework isn't initialized until then.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
            => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        /// <summary>
        /// Avalonia configuration. Also used by the visual designer, so don't remove it.
        /// </summary>
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
