using Frosty.Core;
using Frosty.Core.Controls;
using FrostySdk.Interfaces;
using System;
using System.Windows.Media;
using RimeWidgetBlueprintEditor.Editor;

namespace RimeWidgetBlueprintEditor
{
    public class RimeWidgetBlueprintAssetDefinition : AssetDefinition
    {
        public static readonly ImageSource IconImage = new ImageSourceConverter().ConvertFromString("pack://application:,,,/RimeWidgetBlueprintEditor;component/Images/UITypeIcon.png") as ImageSource;
        protected static ImageSource Icon => IconImage;
        public override ImageSource GetIcon()
        {
            return Icon;
        }

        public override FrostyAssetEditor GetEditor(ILogger logger)
        {
            return new UIEditor(logger);
        }
    }
}