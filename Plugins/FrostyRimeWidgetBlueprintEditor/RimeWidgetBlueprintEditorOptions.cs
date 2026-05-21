using Frosty.Core;
using Frosty.Core.Controls.Editors;
using FrostySdk.Attributes;

namespace RimeWidgetBlueprintEditor
{
    public class FrostyUIEditor : FrostyCustomComboDataEditor<string, string>
    {
    }

    [DisplayName("UI Editor Options")]
    public class UIEditorOptions : OptionsExtension
    {
        [Category("General")]
        [DisplayName("Show Hitboxes")]
        [Description("When hovering over a UI element, this will change whether it will show hitboxes.")]
        public bool ShowHitboxes { get; set; } = true;

        [Category("General")]
        [DisplayName("Show All UI")]
        [Description("If this is on, all UI will be rendered even if the 'Visible' property isn't on")]
        public bool ShowAllUI { get; set; } = false;

        [Category("Movement")]
        [DisplayName("Precise Movement Setting")]
        [Description("Sets the amount of pixels the 'Precise Movement' snaps to (make sure Precise Movement is off to see the change)")]
        public int PreciseMovementSetting { get; set; } = 25;

        [Category("Movement")]
        [DisplayName("Arrow Key Movement Setting")]
        [Description("Sets the amount of pixels arrow keys will move by")]
        public int ArrowKeyMovementSetting { get; set; } = 5;

        //[Category("Movement")]
        //[DisplayName("Use Anchor")]
        //[Description("If true, dragging UI elements will move them with Anchor instead of Offset")]
        //public bool UseAnchor { get; set; } = false;

        [Category("Rendering")]
        [DisplayName("Render Textures")]
        [Description("Textures are used for bitmap entities, they will be rendered if set to true, if not they will be an 'Unrecognized Component'")]
        public bool RenderTextures { get; set; } = true;

        [Category("Rendering")]
        [DisplayName("Render Text")]
        [Description("Text fields are used for text, they will be rendered if set to true, if not they will be an 'Unrecognized Component'")]
        public bool RenderText { get; set; } = true;

        [Category("Rendering")]
        [DisplayName("Render Widgets")]
        [Description("Widgets contain other UI blueprints, they will be rendered if set to true, if not they will be an 'Unrecognized Component' (can make loading times faster)")]
        public bool RenderWidgets { get; set; } = true;

        [Category("Rendering")]
        [DisplayName("Render Font Effects")]
        [Description("Font Effects give color and outlines to text fields, but they can cause lag. If this is off they won't be rendered and text will be white")]
        public bool RenderFontEffects { get; set; } = true;


        public override void Load()
        {
            ShowHitboxes = Config.Get<bool>("ShowHitboxes", true);
            ShowAllUI = Config.Get<bool>("ShowAllUI", false);

            PreciseMovementSetting = Config.Get<int>("PreciseMovementSetting", 25);
            ArrowKeyMovementSetting = Config.Get<int>("ArrowKeyMovementSetting", 5);
            //UseAnchor = Config.Get<bool>("UseAnchor", false);

            RenderTextures = Config.Get<bool>("RenderTextures", true);
            RenderText = Config.Get<bool>("RenderText", true);
            RenderWidgets = Config.Get<bool>("RenderWidgets", true);
            RenderFontEffects = Config.Get<bool>("RenderFontEffects", true);
        }

        public override void Save()
        {
            Config.Add("ShowHitboxes", ShowHitboxes);
            Config.Add("ShowAllUI", ShowAllUI);

            Config.Add("PreciseMovementSetting", PreciseMovementSetting);
            Config.Add("ArrowKeyMovementSetting", ArrowKeyMovementSetting);
            //Config.Add("UseAnchor", UseAnchor);

            Config.Add("RenderTextures", RenderTextures);
            Config.Add("RenderText", RenderText);
            Config.Add("RenderWidgets", RenderWidgets);
            Config.Add("RenderFontEffects", RenderFontEffects);

            Config.Save();
        }
    }
}