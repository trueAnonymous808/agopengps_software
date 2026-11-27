using System;
using System.Collections.Generic;
using System.Drawing;

namespace IconForge
{
    internal sealed class Design
    {
        public float W, H;               // the grid it is drawn on; scaled to the picture's own size
        public Action<Graphics> Draw;
    }

    // The pictures this tool draws, by resource name: the main window's left bar. Every other picture keeps its
    // original file.
    internal static class Icons
    {
        public static readonly Dictionary<string, Design> All = new Dictionary<string, Design>();

        private static void Add(string name, float w, float h, Action<Graphics> draw) => All[name] = new Design { W = w, H = h, Draw = draw };

        static Icons()
        {
            LeftBar.AddAll(Add);
        }
    }
}
