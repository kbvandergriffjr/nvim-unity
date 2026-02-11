using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NvimUniy.Editor;
using Unity.CodeEditor;
using UnityEditor;
using UnityEngine;

namespace NvimUnity
{
    [InitializeOnLoad]
    public class NeovimEditor : IExternalCodeEditor
    {
        public static string defaultApp => EditorPrefs.GetString("kScriptsDefaultApp");
        public static string OS = Utils.GetCurrentOS();
        public static string RootFolder = Utils.GetProjectRoot();
        public static List<string> Analyzers = new List<string>();
        private static Config config;
        private static bool needSaveConfig = false;
        private static bool debugging = false;

        private static string EditorName = "Neovim Code Editor";
        private static string Socket =>
            OS == "Windows" ? 
                @"\\.\pipe\unity2025" : 
                $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.cache/nvimunity{RootFolder.Replace('/', '-')}.sock";

        internal static SdkStyleProjectGeneration projectGenerator;
        private static string analyzerInput;

        static NeovimEditor()
        {
            CodeEditor.Register(new NeovimEditor());
            config = ConfigManager.LoadConfig();
            config.last_project = RootFolder;
            ConfigManager.SaveConfig(config);
            projectGenerator = new SdkStyleProjectGeneration();
        }

        public string GetDisplayName() => EditorName;

        public static bool IsNvimUnityDefaultEditor()
        {
            return string.Equals(defaultApp, Utils.GetLauncherPath());
        }

        public bool OpenProject(string path, int line, int column)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = RootFolder;
            }
            else if (!projectGenerator.IsSupportedFile(path))
            {
                return false;
            }

            if (!IsNvimUnityDefaultEditor())
            {
                return false;
            }

            if (path.Contains(".csproj") && !File.Exists(path))
                SyncAll();

            bool IsRunnigInNeovim = SocketChecker.IsSocketActive(Socket);

            if (line <= 0)
                line = 1;

            if (!IsRunnigInNeovim)
            {
                try
                {
                    if (OS == "Windows")
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = defaultApp,
                            Arguments = $"{path} {line}",
                            UseShellExecute = true,
                            CreateNoWindow = false,
                        };

                        if (debugging)
                            UnityEngine.Debug.Log($"[NvimUnity] Executing: {psi.FileName} {psi.Arguments}");
                        Process.Start(defaultApp, $"{path} {line}");
                    }
                    else
                    {
                        // Original behavior for other OSes
                        ProcessStartInfo psi = Utils.BuildProcessStartInfo(defaultApp, Socket, path, line);
                        if (debugging)
                            UnityEngine.Debug.Log($"[NvimUnity] Executing in terminal: {psi.FileName} {psi.Arguments}");
                        Process.Start(psi);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[NvimUnity] Failed to start App: {ex.Message}");
                    return false;
                }
            }
            else
            {
                return OpenFile(path, line);
            }
        }

        public bool OpenFile(string filePath, int line)
        {
            try
            {
                string cmd = $"<CMD>e +{line} {filePath}<CR>";
                string nvimArgs = $"--server {Socket} --remote-send \"{cmd}\"";
                string nvimPath = Utils.GetNeovimPath();

                var psi = new ProcessStartInfo
                {
                    FileName = nvimPath,
                    Arguments = nvimArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                Process.Start(nvimPath, nvimArgs);
                return true;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[NvimUnity] Failed to start App: {ex.Message}");
                return false;
            }
        }

        public void OnGUI()
        {
            Analyzers = EditorPrefs.GetString("Analyzers").Split(",").ToList();

            GUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            analyzerInput = GUILayout.TextField(analyzerInput);
            if (GUILayout.Button("Add Analyzer", GUILayout.Width(100)))
            {
                if (!String.IsNullOrWhiteSpace(analyzerInput))
                {
                    Analyzers.Add(analyzerInput);

                    var saveString = string.Empty;
                    foreach (var item in Analyzers.ToList())
                    {
                        saveString += $"{item},";
                    }
                    EditorPrefs.SetString("Analyzers", saveString.Trim(','));
                    analyzerInput = string.Empty;
                }
            }
            EditorGUILayout.EndHorizontal();

            foreach (var item in Analyzers.ToList())
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(item);
                if (GUILayout.Button("Remove", GUILayout.Width(100)))
                {
                    Analyzers.Remove(item);

                    var saveString = string.Empty;
                    foreach (var saveItem in Analyzers.ToList())
                    {
                        saveString += $"{saveItem},";
                    }
                    EditorPrefs.SetString("Analyzers", saveString.Trim(','));
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();

            GUILayout.Label("Project Files", EditorStyles.boldLabel);

            if (GUILayout.Button("Regenerate project files"))
            {
                SyncAll();
            }

            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);
        }

        public void Initialize(string editorInstallationPath)
        {
            // Not used by NvimUnity, but required by interface
        }
        
        public CodeEditor.Installation[] Installations => new[]
        {
            new CodeEditor.Installation
            {
                Name = EditorName,
                Path = Utils.GetLauncherPath()
            }
        };

        public void SyncAll()
        {
            projectGenerator.Sync();
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            projectGenerator.SyncIfNeeded(addedFiles.Union(deletedFiles).Union(movedFiles).Union(movedFromFiles), importedFiles);

            foreach (var file in importedFiles.Where(a => Path.GetExtension(a) == ".pdb"))
            {
                var pdbFile = FileUtility.GetAssetFullPath(file);

                // skip Unity packages like com.unity.ext.nunit
                if (pdbFile.IndexOf($"{Path.DirectorySeparatorChar}com.unity.", StringComparison.OrdinalIgnoreCase) > 0)
                    continue;

                var asmFile = Path.ChangeExtension(pdbFile, ".dll");
                if (!File.Exists(asmFile)) // || !Image.IsAssembly(asmFile))
                    continue;

                // if (Symbols.IsPortableSymbolFile(pdbFile))
                //     continue;

                UnityEngine.Debug.LogWarning($"Unity is only able to load mdb or portable-pdb symbols. {file} is using a legacy pdb format.");
            }
        }

        public bool TryGetInstallationForPath(string path, out CodeEditor.Installation installation)
        {
            if (path == Utils.GetLauncherPath())
            {
                installation = new CodeEditor.Installation
                {
                    Name = EditorName,
                    Path = Utils.GetLauncherPath()
                };
                return true;
            }

            installation = default;
            return false;
        }

        public void Save()
        {
            if (needSaveConfig)
            {
                ConfigManager.SaveConfig(config);
                needSaveConfig = false;
            }
        }
    }
}
