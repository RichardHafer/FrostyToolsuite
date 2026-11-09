using System;
using System.Collections.Generic;
using System.Reflection;
using Frosty.Core.Controls;
using Frosty.Core.Managers;
using FrostySdk.Managers.Entries;
using Newtonsoft.Json.Linq;

namespace FrostyEditor.Mcp
{
    /// <summary>
    /// Remembers MCP edits and mirrors them onto the open asset editor's undo stack
    /// so Ctrl+Z and undo_last_change reverse the same change once.
    /// </summary>
    internal static class EbxMcpUndo
    {
        private const int Capacity = 32;

        private static readonly FieldInfo UndoStackField = typeof(UndoManager).GetField("undoStack", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo EditorManagerField = typeof(FrostyBaseAssetEditor).GetField("undoManager", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly List<Entry> Stack = new List<Entry>();

        private sealed class Entry
        {
            public string Asset;
            public string Guid;
            public string AltGuid;
            public string Text;
            public Action Action;
            public IUndoUnit Unit;
            public bool Done;
        }

        public static void Push(string asset, string guid, string text, Action undo)
        {
            Push(asset, guid, null, text, undo);
        }

        public static void Push(string asset, string guid, string altGuid, string text, Action undo)
        {
            Entry entry = new Entry
            {
                Asset = asset ?? "",
                Guid = guid ?? "",
                AltGuid = altGuid ?? "",
                Text = text,
                Action = undo
            };
            Stack.Add(entry);
            while (Stack.Count > Capacity)
                Stack.RemoveAt(0);

            try
            {
                Mirror(entry);
            }
            catch (Exception ex)
            {
                App.Logger?.LogError("MCP undo was not added to the editor history: {0}", ex.GetBaseException().Message);
            }
        }

        public static JObject UndoLast(string asset, string guid)
        {
            DropFinished();
            if (Stack.Count == 0)
                return UndoEditor(asset, guid);

            Entry top = Stack[Stack.Count - 1];
            if (!Matches(top, asset, guid))
            {
                throw new FrostyMcpToolException(
                    "The latest change is \"" + top.Text + "\" on '" + top.Asset + "'. It does not match the requested asset or guid, so nothing was undone.");
            }

            Stack.RemoveAt(Stack.Count - 1);
            top.Done = true;
            Discard(ManagerFor(top.Asset), top.Unit);
            top.Action();
            return Result("mcp", top.Asset, top.Guid, top.Text);
        }

        private static JObject UndoEditor(string asset, string guid)
        {
            FrostyAssetEditor editor = OpenEditor(asset);
            UndoManager manager = Manager(editor);
            IUndoUnit unit = Peek(manager);
            if (unit == null)
                throw new FrostyMcpToolException("There is no change to undo.");
            if (!string.IsNullOrEmpty(guid) && unit.Text.IndexOf(guid, StringComparison.OrdinalIgnoreCase) < 0)
                throw new FrostyMcpToolException("The editor undo \"" + unit.Text + "\" cannot be verified for guid '" + guid + "', so nothing was undone.");
            if (!string.IsNullOrEmpty(asset) && editor?.AssetEntry != null && !string.Equals(editor.AssetEntry.Name, asset, StringComparison.OrdinalIgnoreCase))
                throw new FrostyMcpToolException("The open editor is not '" + asset + "', so nothing was undone.");

            manager.Undo();
            string name = editor?.AssetEntry != null ? editor.AssetEntry.Name : asset;
            return Result("editor", name, guid, unit.Text);
        }

        private static void Mirror(Entry entry)
        {
            UndoManager manager = ManagerFor(entry.Asset);
            if (manager == null)
                return;

            GenericUndoUnit unit = new GenericUndoUnit("MCP: " + entry.Text, delegate { }, delegate { RunFromEditor(entry); });
            entry.Unit = unit;
            manager.CommitUndo(unit);
        }

        private static void RunFromEditor(Entry entry)
        {
            if (entry.Done)
                return;

            entry.Done = true;
            Stack.Remove(entry);
            try
            {
                entry.Action();
            }
            catch (Exception ex)
            {
                App.Logger?.LogError("MCP undo failed: {0}", ex.GetBaseException().Message);
            }
        }

        private static void DropFinished()
        {
            while (Stack.Count > 0 && Stack[Stack.Count - 1].Done)
                Stack.RemoveAt(Stack.Count - 1);
        }

        private static bool Matches(Entry entry, string asset, string guid)
        {
            if (!string.IsNullOrEmpty(asset) && !string.Equals(entry.Asset, asset, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(guid))
                return true;
            return string.Equals(entry.Guid, guid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.AltGuid, guid, StringComparison.OrdinalIgnoreCase);
        }

        private static UndoManager ManagerFor(string asset)
        {
            if (string.IsNullOrEmpty(asset) || App.AssetManager == null)
                return null;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(asset);
            return entry == null ? null : Manager(FrostyMcpTools.FindOpenEditor(entry));
        }

        private static FrostyAssetEditor OpenEditor(string asset)
        {
            if (!string.IsNullOrEmpty(asset) && App.AssetManager != null)
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(asset);
                if (entry != null)
                    return FrostyMcpTools.FindOpenEditor(entry);
            }

            return FrostyMcpTools.FindFocusedEditor();
        }

        private static UndoManager Manager(FrostyAssetEditor editor)
        {
            FrostyBaseAssetEditor assetEditor = editor as FrostyBaseAssetEditor;
            if (assetEditor == null || EditorManagerField == null)
                return null;
            return EditorManagerField.GetValue(assetEditor) as UndoManager;
        }

        private static IUndoUnit Peek(UndoManager manager)
        {
            Stack<IUndoUnit> stack = Units(manager);
            if (stack == null || stack.Count == 0)
                return null;
            return stack.Peek();
        }

        private static void Discard(UndoManager manager, IUndoUnit unit)
        {
            Stack<IUndoUnit> stack = Units(manager);
            if (stack == null || unit == null || stack.Count == 0)
                return;

            IUndoUnit[] items = stack.ToArray();
            stack.Clear();
            for (int i = items.Length - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(items[i], unit))
                    stack.Push(items[i]);
            }
        }

        private static Stack<IUndoUnit> Units(UndoManager manager)
        {
            if (manager == null || UndoStackField == null)
                return null;
            return UndoStackField.GetValue(manager) as Stack<IUndoUnit>;
        }

        private static JObject Result(string source, string asset, string guid, string text)
        {
            JObject result = new JObject
            {
                ["undone"] = true,
                ["source"] = source,
                ["description"] = text ?? ""
            };
            if (!string.IsNullOrEmpty(asset))
                result["asset"] = asset;
            if (!string.IsNullOrEmpty(guid))
                result["guid"] = guid;
            return result;
        }
    }
}
