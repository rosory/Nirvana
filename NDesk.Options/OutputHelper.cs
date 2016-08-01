﻿using System;
using System.IO;

namespace NDesk.Options
{
    public static class OutputHelper
    {
        public static void WriteLabel(string label)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(label);
            Console.ResetColor();
        }

        public static string GetExecutableName()
        {
            return Path.GetFileName(Environment.GetCommandLineArgs()[0]);
        }
    }
}
