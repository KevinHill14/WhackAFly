using System.IO;
using UnityEngine;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace FlyAI
{
    // Opens the system "choose a file" window. Returns the chosen path, or null if it was cancelled.
    public static class FilePicker
    {
        public static string Pick(string startFolder)
        {
            Directory.CreateDirectory(startFolder);
#if UNITY_EDITOR
            var path = UnityEditor.EditorUtility.OpenFilePanelWithFilters("Choose a LiDAR scan", startFolder,
                new[] { "LiDAR scans", "zip,glb", "All files", "*" });
            return string.IsNullOrEmpty(path) ? null : path;
#elif UNITY_STANDALONE_WIN
            return WindowsDialog.Open(startFolder);
#else
            Debug.LogWarning("[FlyAI] The file picker is only available on Windows.");
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        static class WindowsDialog
        {
            const int FileMustExist = 0x1000;
            const int PathMustExist = 0x800;
            const int NoChangeDir = 0x8;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct OpenFileName
            {
                public int structSize;
                public IntPtr owner;
                public IntPtr instance;
                public string filter;
                public string customFilter;
                public int maxCustomFilter;
                public int filterIndex;
                public StringBuilder file;
                public int maxFile;
                public StringBuilder fileTitle;
                public int maxFileTitle;
                public string initialDir;
                public string title;
                public int flags;
                public short fileOffset;
                public short fileExtension;
                public string defaultExtension;
                public IntPtr customData;
                public IntPtr hook;
                public string templateName;
                public IntPtr reserved;
                public int reserved2;
                public int flagsEx;
            }

            [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool GetOpenFileName(ref OpenFileName dialog);

            [DllImport("user32.dll")]
            static extern IntPtr GetActiveWindow();

            public static string Open(string startFolder)
            {
                var dialog = new OpenFileName
                {
                    structSize = Marshal.SizeOf(typeof(OpenFileName)),
                    owner = GetActiveWindow(),
                    filter = "LiDAR scans (*.zip;*.glb)\0*.zip;*.glb\0All files (*.*)\0*.*\0\0",
                    filterIndex = 1,
                    file = new StringBuilder(1024),
                    maxFile = 1024,
                    fileTitle = new StringBuilder(260),
                    maxFileTitle = 260,
                    initialDir = startFolder,
                    title = "Choose a LiDAR scan",
                    flags = FileMustExist | PathMustExist | NoChangeDir,
                };
                return GetOpenFileName(ref dialog) ? dialog.file.ToString() : null;
            }
        }
#endif
    }
}
