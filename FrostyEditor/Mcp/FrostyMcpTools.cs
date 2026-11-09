using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using FrostyEditor.Windows;
using Newtonsoft.Json.Linq;

namespace FrostyEditor.Mcp
{
    internal sealed class FrostyMcpToolException : Exception
    {
        public FrostyMcpToolException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// MCP tools over the editor's loaded asset database.
    /// </summary>
    internal static class FrostyMcpTools
    {
        private static readonly object Gate = new object();

        public static Func<FrostyProject> ProjectProvider { get; set; }

        public static JArray Definitions()
        {
            JArray tools = new JArray
            {
                Tool("get_status",
                    "Report whether a game profile is loaded, the open project, unsaved changes, and the EBX asset count.",
                    new JObject(),
                    null),
                Tool("list_folder",
                    "List one folder of the EBX tree, matching the Data Explorer. An empty path is the root. Returns child folders and the assets stored directly in that folder. Does not recurse.",
                    new JObject
                    {
                        ["path"] = StringProp("Folder path using '/' separators. Empty lists the root."),
                        ["modifiedOnly"] = BoolProp("When true, only modified assets and folders that contain them are returned."),
                        ["limit"] = IntProp("Maximum number of assets to return. Defaults to 500.")
                    },
                    null),
                Tool("search_assets",
                    "Find EBX assets whose full name contains the query. The type filter includes that class and its subclasses.",
                    new JObject
                    {
                        ["query"] = StringProp("Case-insensitive substring of the asset name."),
                        ["type"] = StringProp("EBX class name. Matches the class and anything derived from it."),
                        ["modifiedOnly"] = BoolProp("When true, only modified assets are returned."),
                        ["limit"] = IntProp("Maximum number of matches. Defaults to 50.")
                    },
                    null),
                Tool("get_asset",
                    "Read one EBX asset by its full name. Objects are flat. Pointers are internal guids or external asset names and are not expanded. Large arrays and the payload are truncated.",
                    new JObject
                    {
                        ["name"] = StringProp("Full EBX asset name, as shown in the Data Explorer."),
                        ["maxArrayItems"] = IntProp("Maximum items kept from each array. Defaults to 64.")
                    },
                    new[] { "name" }),
                Tool("get_object",
                    "Read one object from an EBX asset by the object guid returned from get_asset. Use this when get_asset was truncated.",
                    new JObject
                    {
                        ["name"] = StringProp("Full EBX asset name."),
                        ["guid"] = StringProp("Object guid from get_asset."),
                        ["maxArrayItems"] = IntProp("Maximum items kept from each array. Defaults to 64.")
                    },
                    new[] { "name", "guid" }),
                Tool("clone_layer_object",
                    "Clone one entity from a LayerData asset into another LayerData asset. Preserve all fields and external references, assign a unique GUID, append it to the target layer Objects array, and mark the project dirty.",
                    new JObject
                    {
                        ["sourceAsset"] = StringProp("Full name of the source LayerData asset."),
                        ["sourceGuid"] = StringProp("GUID of the entity in the source layer Objects array."),
                        ["targetAsset"] = StringProp("Full name of the target LayerData asset.")
                    },
                    new[] { "sourceAsset", "sourceGuid", "targetAsset" },
                    false),
                Tool("remove_layer_object",
                    "Remove exactly one entity by GUID from the Objects array of a LayerData asset. Mark the project dirty.",
                    new JObject
                    {
                        ["asset"] = StringProp("Full name of the LayerData asset."),
                        ["guid"] = StringProp("GUID of the entity to remove from the Objects array.")
                    },
                    new[] { "asset", "guid" },
                    false),
                Tool("save_project",
                    "Save the currently open Frosty project using the editor's project save API.",
                    new JObject(),
                    null,
                    false),
                Tool("export_mod",
                    "Export the currently open project as a Frosty mod using the editor's native export API and existing mod settings. Do not overwrite an existing file unless explicitly allowed.",
                    new JObject
                    {
                        ["outputPath"] = StringProp("Destination .fbmod path."),
                        ["overwrite"] = new JObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Replace an existing file. Defaults to false.",
                            ["default"] = false
                        }
                    },
                    new[] { "outputPath" },
                    false)
            };
            EbxMcpEdit.AddTools(tools);
            return tools;
        }

        public static JObject Call(string name, JObject args)
        {
            if (args == null)
                args = new JObject();

            lock (Gate)
            {
                switch (name)
                {
                    case "get_status":
                        return GetStatus();
                    case "list_folder":
                        return ListFolder(args);
                    case "search_assets":
                        return SearchAssets(args);
                    case "get_asset":
                        return GetAsset(args);
                    case "get_object":
                        return GetObject(args);
                    case "clone_layer_object":
                        return CloneLayerObject(args);
                    case "remove_layer_object":
                        return RemoveLayerObject(args);
                    case "save_project":
                        return SaveProject();
                    case "export_mod":
                        return ExportMod(args);
                    default:
                        JObject handled;
                        if (EbxMcpEdit.TryCall(name, args, out handled))
                            return handled;
                        throw new FrostyMcpToolException("Unknown tool '" + name + "'.");
                }
            }
        }

        private static JObject GetStatus()
        {
            if (!DatabaseOpen())
                return NotLoaded();

            FrostyProject project = ProjectProvider?.Invoke();
            bool dirty;
            uint ebxCount;
            try
            {
                dirty = project != null && project.IsDirty;
                ebxCount = App.AssetManager.GetEbxCount();
            }
            catch (InvalidOperationException)
            {
                throw new FrostyMcpToolException("The asset database changed while it was being read. Retry.");
            }

            return new JObject
            {
                ["loaded"] = true,
                ["profile"] = ProfilesLibrary.DisplayName,
                ["project"] = project != null ? project.DisplayName : "",
                ["filename"] = project != null ? project.Filename ?? "" : "",
                ["dirty"] = dirty,
                ["ebxCount"] = ebxCount
            };
        }

        private static JObject ListFolder(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            string path = NormalizePath(ArgString(args, "path"));
            bool modifiedOnly = ArgBool(args, "modifiedOnly", false);
            int limit = ArgInt(args, "limit", 500, 1, 2000);

            Dictionary<string, string> folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<EbxAssetEntry> matched = new List<EbxAssetEntry>();

            foreach (EbxAssetEntry entry in Snapshot(null))
            {
                if (modifiedOnly && !entry.IsModified)
                    continue;

                string entryPath = entry.Path ?? "";
                if (path.Length == 0)
                {
                    if (entryPath.Length == 0)
                        matched.Add(entry);
                    else
                        AddFolder(folders, entryPath, 0);
                }
                else if (entryPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(entry);
                }
                else if (entryPath.Length > path.Length + 1 &&
                         entryPath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
                {
                    AddFolder(folders, entryPath, path.Length + 1);
                }
            }

            matched.Sort(delegate (EbxAssetEntry left, EbxAssetEntry right)
            {
                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            int assetCount = matched.Count;
            if (matched.Count > limit)
                matched.RemoveRange(limit, matched.Count - limit);

            JArray assets = new JArray();
            foreach (EbxAssetEntry entry in matched)
                assets.Add(Describe(entry));

            List<string> folderNames = new List<string>(folders.Values);
            folderNames.Sort(StringComparer.OrdinalIgnoreCase);

            return new JObject
            {
                ["path"] = path,
                ["folders"] = new JArray(folderNames),
                ["assets"] = assets,
                ["assetCount"] = assetCount,
                ["truncated"] = assetCount > assets.Count
            };
        }

        private static JObject SearchAssets(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            string query = ArgString(args, "query");
            string type = ArgString(args, "type");
            bool modifiedOnly = ArgBool(args, "modifiedOnly", false);
            int limit = ArgInt(args, "limit", 50, 1, 200);

            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(type) && !modifiedOnly)
                throw new FrostyMcpToolException("Provide a query, a type, or modifiedOnly.");

            query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            type = string.IsNullOrWhiteSpace(type) ? null : type.Trim();

            List<EbxAssetEntry> matched = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry entry in Snapshot(type))
            {
                if (modifiedOnly && !entry.IsModified)
                    continue;
                if (query != null && (entry.Name == null || entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                matched.Add(entry);
            }

            matched.Sort(delegate (EbxAssetEntry left, EbxAssetEntry right)
            {
                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

            int total = matched.Count;
            if (matched.Count > limit)
                matched.RemoveRange(limit, matched.Count - limit);

            JArray assets = new JArray();
            foreach (EbxAssetEntry entry in matched)
                assets.Add(Describe(entry));

            return new JObject
            {
                ["total"] = total,
                ["truncated"] = total > assets.Count,
                ["assets"] = assets
            };
        }

        private static JObject GetAsset(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            string name = RequiredString(args, "name");
            int maxArrayItems = ArgInt(args, "maxArrayItems", EbxMcpSerializer.DefaultMaxArrayItems, 0, 256);
            EbxAssetEntry entry = RequireEntry(name);
            EbxAsset asset = ReadAsset(entry);
            return EbxMcpSerializer.SerializeAsset(asset, entry, App.AssetManager, EbxMcpSerializer.DefaultMaxChars, maxArrayItems);
        }

        private static JObject GetObject(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            string name = RequiredString(args, "name");
            string guid = RequiredString(args, "guid");
            int maxArrayItems = ArgInt(args, "maxArrayItems", EbxMcpSerializer.DefaultMaxArrayItems, 0, 256);
            EbxAssetEntry entry = RequireEntry(name);
            EbxAsset asset = ReadAsset(entry);

            foreach (object instance in asset.Objects)
            {
                if (EbxMcpSerializer.GuidEquals(instance, guid))
                    return EbxMcpSerializer.SerializeObject(instance, entry, App.AssetManager, EbxMcpSerializer.DefaultMaxChars, maxArrayItems);
            }

            throw new FrostyMcpToolException("Object '" + guid + "' was not found in '" + entry.Name + "'.");
        }

        internal static EbxAsset ReadAsset(EbxAssetEntry entry)
        {
            EbxAsset asset;
            try
            {
                asset = App.AssetManager.GetEbx(entry);
            }
            catch (Exception ex)
            {
                throw new FrostyMcpToolException("Failed to read '" + entry.Name + "': " + ex.GetBaseException().Message);
            }

            if (asset == null || !asset.IsValid)
                throw new FrostyMcpToolException("Asset '" + entry.Name + "' has no objects.");
            return asset;
        }

        internal static EbxAssetEntry RequireEntry(string name)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
                throw new FrostyMcpToolException("EBX asset '" + name + "' was not found.");
            return entry;
        }

        private static List<EbxAssetEntry> Snapshot(string type)
        {
            try
            {
                List<EbxAssetEntry> entries = new List<EbxAssetEntry>();
                IEnumerable<EbxAssetEntry> source = string.IsNullOrEmpty(type)
                    ? App.AssetManager.EnumerateEbx()
                    : App.AssetManager.EnumerateEbx(type);
                foreach (EbxAssetEntry entry in source)
                    entries.Add(entry);
                return entries;
            }
            catch (InvalidOperationException)
            {
                throw new FrostyMcpToolException("The asset database changed while it was being read. Retry.");
            }
        }

        private static void AddFolder(Dictionary<string, string> folders, string entryPath, int start)
        {
            int slash = entryPath.IndexOf('/', start);
            string name = slash < 0 ? entryPath.Substring(start) : entryPath.Substring(start, slash - start);
            if (name.Length == 0 || folders.ContainsKey(name))
                return;
            folders.Add(name, name);
        }

        private static JObject Describe(EbxAssetEntry entry)
        {
            return new JObject
            {
                ["name"] = entry.Name,
                ["type"] = entry.Type ?? "",
                ["guid"] = entry.Guid.ToString(),
                ["modified"] = entry.IsModified
            };
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
                return "";

            path = path.Replace('\\', '/').Trim();
            while (path.StartsWith("/", StringComparison.Ordinal))
                path = path.Substring(1);
            while (path.EndsWith("/", StringComparison.Ordinal))
                path = path.Substring(0, path.Length - 1);
            return path;
        }

        private static JObject CloneLayerObject(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            string sourceName = RequiredString(args, "sourceAsset");
            string sourceGuid = RequiredString(args, "sourceGuid");
            string targetName = RequiredString(args, "targetAsset");
            return OnUi(() => CloneLayerObjectOnUi(sourceName, sourceGuid, targetName));
        }

        private static JObject CloneLayerObjectOnUi(string sourceName, string sourceGuid, string targetName)
        {
            EbxAssetEntry sourceEntry = RequireLayer(sourceName);
            EbxAssetEntry targetEntry = RequireLayer(targetName);
            EbxAsset sourceAsset = LoadEditable(sourceEntry);
            EbxAsset targetAsset = ReferenceEquals(sourceEntry, targetEntry) ? sourceAsset : LoadEditable(targetEntry);

            int index;
            object entity = EbxObjectCloner.FindLayerEntity(sourceAsset, sourceGuid, out index);
            if (entity == null)
                throw new FrostyMcpToolException("No Objects entry in '" + sourceEntry.Name + "' matches '" + sourceGuid + "'.");

            object clone = EbxObjectCloner.CloneInto(entity, sourceAsset, sourceEntry, targetAsset, targetEntry);
            EnsureAssetObject(targetAsset, clone);
            EbxObjectCloner.LayerObjects(targetAsset).Add(new PointerRef(clone));
            CommitAsset(targetAsset, targetEntry);

            AssetClassGuid guid = ((dynamic)clone).GetInstanceGuid();
            object cloned = clone;
            EbxMcpUndo.Push(targetEntry.Name, guid.ToString(), sourceGuid, "Clone layer object into " + targetEntry.Name, delegate
            {
                EbxAsset live = LoadEditable(targetEntry);
                IList objects = EbxObjectCloner.LayerObjects(live);
                for (int i = objects.Count - 1; i >= 0; i--)
                {
                    object item = objects[i];
                    if (item is PointerRef && ReferenceEquals(((PointerRef)item).Internal, cloned))
                    {
                        objects.RemoveAt(i);
                        break;
                    }
                }
                live.RemoveObject(cloned);
                CommitAsset(live, targetEntry);
            });
            App.Logger?.Log("Cloned layer object {0} from {1} into {2}", guid.ToString(), sourceEntry.Name, targetEntry.Name);
            return new JObject
            {
                ["sourceAsset"] = sourceEntry.Name,
                ["targetAsset"] = targetEntry.Name,
                ["guid"] = guid.ToString(),
                ["type"] = clone.GetType().Name,
                ["dirty"] = true
            };
        }

        private static JObject RemoveLayerObject(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            string name = RequiredString(args, "asset");
            string guid = RequiredString(args, "guid");
            return OnUi(() => RemoveLayerObjectOnUi(name, guid));
        }

        private static JObject RemoveLayerObjectOnUi(string name, string guid)
        {
            EbxAssetEntry entry = RequireLayer(name);
            EbxAsset asset = LoadEditable(entry);
            int index;
            object entity = EbxObjectCloner.FindLayerEntity(asset, guid, out index);
            if (entity == null)
                throw new FrostyMcpToolException("No Objects entry in '" + entry.Name + "' matches '" + guid + "'.");

            EbxObjectCloner.LayerObjects(asset).RemoveAt(index);
            asset.RemoveObject(entity);
            CommitAsset(asset, entry);

            int removedAt = index;
            object removed = entity;
            EbxMcpUndo.Push(entry.Name, guid, "Remove layer object " + guid, delegate
            {
                EbxAsset live = LoadEditable(entry);
                bool present = false;
                foreach (object existing in live.Objects)
                {
                    if (ReferenceEquals(existing, removed))
                    {
                        present = true;
                        break;
                    }
                }
                if (!present)
                    live.AddObject(removed);
                IList objects = EbxObjectCloner.LayerObjects(live);
                int insertAt = removedAt > objects.Count ? objects.Count : removedAt;
                objects.Insert(insertAt, new PointerRef(removed));
                CommitAsset(live, entry);
            });
            App.Logger?.Log("Removed layer object {0} from {1}", guid, entry.Name);
            return new JObject
            {
                ["asset"] = entry.Name,
                ["guid"] = guid,
                ["type"] = entity.GetType().Name,
                ["dirty"] = true
            };
        }

        private static JObject SaveProject()
        {
            if (!DatabaseOpen())
                return NotLoaded();

            FrostyProject project = RequireProject();
            if (string.IsNullOrEmpty(project.Filename))
                throw new FrostyMcpToolException("This project has never been saved. Save it once in the editor, then try again.");

            project.Save();
            Log("Project saved to {0}", project.Filename);
            return new JObject
            {
                ["filename"] = project.Filename,
                ["saved"] = true
            };
        }

        private static JObject ExportMod(JObject args)
        {
            if (!DatabaseOpen())
                return NotLoaded();

            FrostyProject project = RequireProject();
            string outputPath = RequiredString(args, "outputPath");
            bool overwrite = ArgBool(args, "overwrite", false);

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(outputPath);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
            {
                throw new FrostyMcpToolException("'" + outputPath + "' is not a valid path.");
            }

            if (File.Exists(fullPath) && !overwrite)
                throw new FrostyMcpToolException("'" + fullPath + "' already exists. Pass overwrite true to replace it.");

            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            project.WriteToMod(fullPath, project.GetModSettings());
            Log("Mod saved to {0}", fullPath);
            return new JObject
            {
                ["outputPath"] = fullPath,
                ["exported"] = true
            };
        }

        private static EbxAssetEntry RequireLayer(string name)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null)
                throw new FrostyMcpToolException("Asset '" + name + "' was not found.");
            if (!TypeLibrary.IsSubClassOf(entry.Type, "LayerData"))
                throw new FrostyMcpToolException("Asset '" + entry.Name + "' is " + entry.Type + ", not a LayerData asset.");
            return entry;
        }

        internal static EbxAsset LoadEditable(EbxAssetEntry entry)
        {
            FrostyAssetEditor editor = FindOpenEditor(entry);
            if (editor?.Asset != null)
                return editor.Asset;
            return ReadAsset(entry);
        }

        internal static FrostyAssetEditor FindOpenEditor(EbxAssetEntry entry)
        {
            MainWindow window = Application.Current?.MainWindow as MainWindow;
            if (window?.TabControl == null || entry == null)
                return null;

            foreach (object item in window.TabControl.Items)
            {
                FrostyTabItem tab = item as FrostyTabItem;
                FrostyAssetEditor editor = tab?.Content as FrostyAssetEditor;
                if (editor?.Asset != null && editor.AssetEntry == entry)
                    return editor;
            }

            return null;
        }

        internal static FrostyAssetEditor FindFocusedEditor()
        {
            MainWindow window = Application.Current?.MainWindow as MainWindow;
            if (window?.TabControl == null)
                return null;

            FrostyTabItem tab = window.TabControl.SelectedItem as FrostyTabItem;
            return tab?.Content as FrostyAssetEditor;
        }

        private static void EnsureAssetObject(EbxAsset asset, object instance)
        {
            foreach (object existing in asset.Objects)
            {
                if (ReferenceEquals(existing, instance))
                    return;
            }

            asset.AddObject(instance);
        }

        internal static void CommitAsset(EbxAsset asset, EbxAssetEntry entry)
        {
            asset.Update();
            App.AssetManager.ModifyEbx(entry.Name, asset);
            if (entry.ModifiedEntry != null)
            {
                entry.ModifiedEntry.DependentAssets.Clear();
                entry.ModifiedEntry.DependentAssets.AddRange(asset.Dependencies);
            }

            Frosty.Core.App.EditorWindow?.DataExplorer?.RefreshItems();
        }

        private static FrostyProject RequireProject()
        {
            FrostyProject project = ProjectProvider?.Invoke();
            if (project == null)
                throw new FrostyMcpToolException("No project is open.");
            return project;
        }

        internal static JObject OnUi(Func<JObject> action)
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                return action();

            return (JObject)dispatcher.Invoke(action);
        }

        private static void Log(string message, params object[] args)
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                App.Logger?.Log(message, args);
                return;
            }

            dispatcher.BeginInvoke(new Action(() => App.Logger?.Log(message, args)));
        }

        private static bool DatabaseOpen()
        {
            return ProfilesLibrary.HasLoadedProfile && App.AssetManager != null;
        }

        private static JObject NotLoaded()
        {
            return new JObject
            {
                ["loaded"] = false,
                ["message"] = "Game profile is not loaded."
            };
        }

        internal static JObject Tool(string name, string description, JObject properties, string[] required, bool additionalProperties = true)
        {
            JObject schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required != null && required.Length > 0)
                schema["required"] = new JArray(required);
            if (!additionalProperties)
                schema["additionalProperties"] = false;

            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = schema
            };
        }

        private static JObject StringProp(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        private static JObject BoolProp(string description)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description };
        }

        private static JObject IntProp(string description)
        {
            return new JObject { ["type"] = "integer", ["description"] = description };
        }

        internal static string ArgString(JObject args, string name)
        {
            JToken token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            return token.ToString();
        }

        internal static string RequiredString(JObject args, string name)
        {
            string value = ArgString(args, name);
            if (string.IsNullOrWhiteSpace(value))
                throw new FrostyMcpToolException("'" + name + "' is required.");
            return value.Trim();
        }

        internal static bool ArgBool(JObject args, string name, bool fallback)
        {
            JToken token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();

            bool parsed;
            return bool.TryParse(token.ToString(), out parsed) ? parsed : fallback;
        }

        private static int ArgInt(JObject args, string name, int fallback, int min, int max)
        {
            JToken token = args?[name];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;

            int value;
            if (token.Type == JTokenType.Integer)
                value = token.Value<int>();
            else if (!int.TryParse(token.ToString(), out value))
                return fallback;

            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}
