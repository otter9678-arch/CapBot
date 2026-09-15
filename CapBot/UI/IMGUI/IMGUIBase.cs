using System;
using UnityEngine;

namespace CapBot.UI.IMGUI
{
    public abstract class IMGUIBase
    {
        public bool Visible = false;
        protected Rect WindowRect = new Rect(200, 200, 500, 600);

        public void Toggle()
        {
            Visible = !Visible;
        }

        public void Draw()
        {
            if (!Visible) return;
            WindowRect = GUI.Window(GetHashCode(), WindowRect, DrawWindow, GetTitle());
        }

        protected abstract string GetTitle();
        protected abstract void DrawWindow(int id);
    }
}