using System;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using SDGraphics;
using SDUtils;
using Ship_Game.Audio;
using Ship_Game.Graphics;
using Color = Microsoft.Xna.Framework.Color;

namespace Ship_Game;

public sealed class GlobalConstructionScreen : GameScreen
{
    static readonly Color Gold = new(219, 188, 126);
    static readonly Color Cyan = new(108, 207, 215);
    static readonly Color Muted = new(158, 177, 189);
    readonly UniverseScreen Universe;
    ScrollList<BuildingRow> Catalog;
    GlobalConstruction.CatalogSnapshot Snapshot;
    GlobalConstruction.CatalogEntry Selected;
    SubTexture Background;
    RectF Window, TableHeader, Details, SearchRect;
    ActionButton BuildButton;
    string Search = "", Filter = "All";
    string Status = "Surveying your colonies...";
    bool Busy;
    bool StatusError;
    int VisibleBuildings;

    public GlobalConstructionScreen(EmpireManagementScreen parent) : base(parent, toPause: parent.Universe)
    {
        Universe = parent.Universe;
        IsPopup = true;
        TransitionOnTime = TransitionOffTime = 0.2f;
    }

    public override void LoadContent()
    {
        Background = ResourceManager.Texture("NewUI/GlobalConstruction/background");
        Window = new RectF(18, 42, ScreenWidth - 36, ScreenHeight - 60);
        float toolbarY = Window.Y + Math.Clamp(Window.H * 0.22f, 130, 200);
        float left = Window.X + 26, width = Window.W - 52;
        SearchRect = new RectF(left, toolbarY, 230, 34);
        var search = Add(new UITextEntry(new RectF(left + 12, toolbarY + 8, 204, 22), Fonts.Arial12Bold, ""));
        search.Name = "BuildingSearch";
        search.Color = Color.White;
        search.OnTextChanged = value => { Search = value; PopulateCatalog(); };

        string[] filters = { "All", "Industry", "Research", "Food", "Military", "Other" };
        float buttonWidth = Math.Min(100, (width - 248) / filters.Length);
        for (int i = 0; i < filters.Length; ++i)
        {
            string filter = filters[i];
            Add(new ActionButton(new RectF(left + 248 + i * buttonWidth, toolbarY, buttonWidth - 5, 34), filter,
                () => { Filter = filter; PopulateCatalog(); }, () => Filter == filter));
        }
        TableHeader = new RectF(left, toolbarY + 46, width, 32);
        float detailHeight = ScreenHeight < 800 ? 140 : 156;
        Details = new RectF(left, Window.Bottom - detailHeight - 64, width, detailHeight);
        Catalog = Add(new ScrollList<BuildingRow>(new RectF(left, TableHeader.Bottom, width, Details.Y - TableHeader.Bottom - 12),
            ScreenHeight < 800 ? 64 : 76, ListStyle.Blue));
        Catalog.Name = "BuildingCatalog";
        Catalog.OnClick = row => { Selected = row.Entry; UpdateBuildButton(); };
        Catalog.OnDoubleClick = row => { Selected = row.Entry; QueueBuilding(row.Entry.Building); };
        BuildButton = Add(new ActionButton(new RectF(Window.Right - 260, Window.Bottom - 48, 230, 34),
            "Build on best colony", () => { if (Selected != null) QueueBuilding(Selected.Building); }));
        CloseButton(Window.Right - 42, Window.Y + 16);
        RefreshCatalog();
    }

    void RefreshCatalog()
    {
        Busy = true;
        UpdateBuildButton();
        Universe.RunOnSimThread(() =>
        {
            var snapshot = GlobalConstruction.GetCatalog(Universe.Player);
            RunOnNextFrame(() =>
            {
                ApplySnapshot(snapshot);
                Status = "Double-click a building to queue one copy on the best eligible colony.";
            });
        });
    }

    void ApplySnapshot(GlobalConstruction.CatalogSnapshot snapshot)
    {
        int selectedId = Selected?.Building.BID ?? -1;
        Snapshot = snapshot;
        Busy = false;
        Selected = snapshot.Entries.FirstOrDefault(e => e.Building.BID == selectedId);
        PopulateCatalog();
    }

    static string Group(Building building) => building.IsCapitalOrOutpost || building.IsTerraformer || building.IsBiospheres ? "Other"
        : building.IsMilitary ? "Military" : building.ProducesProduction ? "Industry"
        : building.ProducesResearch ? "Research" : building.ProducesFood ? "Food"
        : "Other";

    void PopulateCatalog()
    {
        if (Catalog == null || Snapshot == null) return;
        Catalog.Reset();
        var entries = Snapshot.Entries.Where(e => (Filter == "All" || Group(e.Building) == Filter)
            && e.Building.TranslatedName.Text.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
        VisibleBuildings = entries.Length;
        if (Selected == null || !entries.Contains(Selected)) Selected = entries.FirstOrDefault();
        foreach (var entry in entries) Catalog.AddItem(new BuildingRow(this, entry));
        UpdateBuildButton();
    }

    void UpdateBuildButton()
    {
        if (BuildButton == null) return;
        BuildButton.Enabled = !Busy && Selected?.Best != null;
        BuildButton.Caption = Busy ? "Evaluating colonies..." : Selected?.Best == null ? "No eligible colony" : "Build on best colony";
    }

    void QueueBuilding(Building building)
    {
        if (Busy) return;
        Busy = true;
        StatusError = false;
        Status = $"Finding the best colony for {building.TranslatedName.Text}...";
        UpdateBuildButton();
        Universe.RunOnSimThread(() =>
        {
            var destination = GlobalConstruction.QueueBest(Universe.Player, building);
            var snapshot = GlobalConstruction.GetCatalog(Universe.Player);
            RunOnNextFrame(() =>
            {
                ApplySnapshot(snapshot);
                StatusError = destination == null;
                Status = destination == null
                    ? $"Cannot queue {building.TranslatedName.Text}: no eligible colony with a valid building slot."
                    : $"Queued {building.TranslatedName.Text} on {destination.Planet.Name}. Delivery: {Eta(destination.Turns)}.";
                if (destination == null) GameAudio.NegativeClick();
                else GameAudio.AcceptClick();
            });
        });
    }

    static string Eta(float turns) => float.IsPositiveInfinity(turns) ? "stalled" : $"~{Math.Ceiling(turns):0} turns";

    public override void Draw(SpriteBatch batch, DrawTimes elapsed)
    {
        ScreenManager.FadeBackBufferToBlack(TransitionAlpha * 2 / 3);
        batch.SafeBegin();
        batch.Draw(Background, Window, Color.White);
        batch.FillRectangle(new RectF(Window.X + 18, Window.Y + 18, Math.Min(Window.W - 85, 665), 113), new Color(4, 13, 21).Alpha(0.82f));
        Text(batch, "EMPIRE / INFRASTRUCTURE", Window.X + 32, Window.Y + 28, Window.W - 100, Fonts.Arial12Bold, Gold);
        Text(batch, "GLOBAL CONSTRUCTION", Window.X + 30, Window.Y + 53, Window.W - 100, Fonts.Pirulen20, Colors.Cream);
        Text(batch, "Double-click a building. Your governors handle placement.", Window.X + 32, Window.Y + 91, Window.W - 100, Fonts.Arial12, Muted);
        if (Snapshot != null)
        {
            batch.FillRectangle(new RectF(Window.X + 26, SearchRect.Y - 35, Math.Min(640, Window.W - 52), 27), new Color(4, 13, 21).Alpha(.9f));
            Text(batch, $"{Snapshot.Colonies} COLONIES     /     {Snapshot.TotalBuilt} BUILT     /     {Snapshot.TotalQueued} IN CONSTRUCTION",
                Window.X + 32, SearchRect.Y - 29, Window.W - 100, Fonts.Arial12Bold, Gold);
        }

        DrawPanel(batch, SearchRect, false);
        if (Search.Length == 0 && Find<UITextEntry>("BuildingSearch", out var search) && !search.HandlingInput)
            Text(batch, "Search buildings...", SearchRect.X + 12, SearchRect.Y + 8, SearchRect.W - 20, Fonts.Arial12, Muted);
        batch.FillRectangle(TableHeader, new Color(17, 32, 43).Alpha(0.96f));
        float x = TableHeader.X + 8, w = TableHeader.W - 32;
        Text(batch, $"BUILDING  /  {VisibleBuildings}", x + 10, TableHeader.Y + 8, w * .39f, Fonts.Arial12Bold, Muted);
        Text(batch, "IN EMPIRE", x + w * .42f, TableHeader.Y + 8, w * .1f, Fonts.Arial12Bold, Gold);
        Text(batch, "QUEUED", x + w * .54f, TableHeader.Y + 8, w * .1f, Fonts.Arial12Bold, Cyan);
        Text(batch, "AI DESTINATION", x + w * .65f, TableHeader.Y + 8, w * .23f, Fonts.Arial12Bold, Muted);
        Text(batch, "DELIVERY", x + w * .89f, TableHeader.Y + 8, w * .11f, Fonts.Arial12Bold, Muted);
        batch.FillRectangle(Catalog.RectF, new Color(5, 14, 23).Alpha(0.68f));
        if (VisibleBuildings == 0 && !Busy)
            Text(batch, "No buildings match this search or category.", Catalog.X + 24, Catalog.Y + 32, Catalog.Width - 48, Fonts.Arial14Bold, Muted);
        DrawDetails(batch);
        batch.FillRectangle(new RectF(Window.X + 26, Window.Bottom - 54, Window.W - 52, 46), new Color(4, 13, 21).Alpha(.96f));
        Text(batch, Status, Window.X + 28, Window.Bottom - 48, Window.W - 302, Fonts.Arial12Bold, StatusError ? Color.SandyBrown : Cyan);
        Text(batch, "Estimates include queue workload. Cancel queued buildings from their colony screens.",
            Window.X + 28, Window.Bottom - 26, Window.W - 302, Fonts.Arial10, Muted);
        base.Draw(batch, elapsed);
        batch.SafeEnd();
    }

    void DrawDetails(SpriteBatch batch)
    {
        DrawPanel(batch, Details, false);
        if (Selected == null)
        {
            Text(batch, "Select a building to inspect its recommended colony.", Details.X + 20, Details.Y + 20, Details.W - 40, Fonts.Arial14Bold, Muted);
            return;
        }
        Building building = Selected.Building;
        var best = Selected.Best;
        batch.Draw(building.IconTex, new RectF(Details.X + 16, Details.Y + 16, 54, 54), Color.White);
        Text(batch, building.TranslatedName.Text, Details.X + 84, Details.Y + 14, Details.W * .43f - 84, Fonts.Arial14Bold, Colors.Cream);
        Text(batch, $"{Selected.Built} built  /  {Selected.Queued} queued  /  {Selected.EligibleColonies} eligible colonies",
            Details.X + 84, Details.Y + 39, Details.W * .43f - 84, Fonts.Arial12, Gold);
        string heading = best == null ? "NO ELIGIBLE DESTINATION" : $"NEXT BUILD / {best.Planet.Name}";
        Text(batch, heading, Details.X + Details.W * .47f, Details.Y + 14, Details.W * .53f - 20, Fonts.Arial14Bold, Cyan);
        Text(batch, best == null ? "Check unlocks, uniqueness limits and free building slots."
            : $"{best.Planet.CType} colony  /  {(best.AtRisk ? "Combat or sabotage risk" : "No current combat or sabotage")}",
            Details.X + Details.W * .47f, Details.Y + 39, Details.W * .53f - 20, Fonts.Arial12, Muted);
        Text(batch, building.DescriptionText.Text.Replace('\n', ' '), Details.X + 20, Details.Y + 75, Details.W - 40, Fonts.Arial12, Muted);
        if (best != null)
        {
            string[] metrics = { $"DELIVERY  {Eta(best.Turns)}", $"PRODUCTION  {best.Production:0.#}/turn", $"OPEN SLOTS  {best.FreeSlots}", $"QUEUE AHEAD  {best.QueueDepth}" };
            for (int i = 0; i < metrics.Length; ++i)
                Text(batch, metrics[i], Details.X + 20 + i * (Details.W - 40) / 4, Details.Bottom - 29, (Details.W - 40) / 4 - 10, Fonts.Arial12Bold, i == 0 ? Gold : Cyan);
        }
    }

    static void DrawPanel(SpriteBatch batch, RectF rect, bool active)
    {
        batch.FillRectangle(rect, (active ? new Color(30, 63, 74) : new Color(8, 21, 31)).Alpha(0.92f));
        batch.DrawRectangle(rect, (active ? Cyan : Gold).Alpha(active ? .75f : .32f));
    }

    static void Text(SpriteBatch batch, string value, float x, float y, float width, Font font, Color color)
    {
        string text = value ?? "";
        while (text.Length > 3 && font.TextWidth(text) > width)
            text = text.Substring(0, text.Length - 4) + "...";
        batch.DrawString(font, text, new Vector2(x, y), color);
    }

    sealed class BuildingRow : ScrollListItem<BuildingRow>
    {
        readonly GlobalConstructionScreen Screen;
        public readonly GlobalConstruction.CatalogEntry Entry;
        public BuildingRow(GlobalConstructionScreen screen, GlobalConstruction.CatalogEntry entry)
        {
            Screen = screen;
            Entry = entry;
            Name = $"Building-{entry.Building.BID}";
        }

        public override void Draw(SpriteBatch batch, DrawTimes elapsed)
        {
            bool selected = Screen.Selected == Entry;
            Color fill = selected ? new Color(24, 55, 68) : Hovered ? new Color(22, 40, 53) : new Color(8, 20, 31);
            batch.FillRectangle(Rect, fill.Alpha(selected || Hovered ? .94f : ItemIndex % 2 == 0 ? .74f : .42f));
            batch.FillRectangle(new RectF(X, Bottom - 1, Width, 1), Gold.Alpha(.16f));
            if (selected) batch.FillRectangle(new RectF(X, Y, 3, Height), Cyan);
            float iconSize = Math.Min(50, Height - 14);
            DrawPanel(batch, new RectF(X + 10, Y + 6, iconSize + 2, iconSize + 2), selected);
            batch.Draw(Entry.Building.IconTex, new RectF(X + 11, Y + 7, iconSize, iconSize), Color.White);
            float textX = X + iconSize + 25;
            Text(batch, Entry.Building.TranslatedName.Text, textX, Y + 10, Width * .4f - iconSize - 30, Fonts.Arial14Bold, Colors.Cream);
            Text(batch, $"{Group(Entry.Building)}  /  {Entry.Building.ActualCost(Screen.Universe.Player):0} prod  /  {Entry.Building.ActualMaintenance(Screen.Universe.Player):0.#} upkeep",
                textX, Y + 36, Width * .4f - iconSize - 30, Fonts.Arial12, Muted);
            Text(batch, Entry.Built.ToString(), X + Width * .42f + 16, Y + 18, Width * .1f - 16, Fonts.Arial20Bold, Gold);
            Text(batch, Entry.Queued.ToString(), X + Width * .54f + 12, Y + 18, Width * .1f - 12, Fonts.Arial20Bold, Entry.Queued > 0 ? Cyan : Muted);
            var best = Entry.Best;
            Text(batch, best?.Planet.Name ?? "Unavailable", X + Width * .65f, Y + 12, Width * .23f, Fonts.Arial14Bold, best == null ? Muted : Cyan);
            Text(batch, best == null ? "No eligible colony" : $"{best.FreeSlots} slots / {best.QueueDepth} in queue",
                X + Width * .65f, Y + 37, Width * .23f, Fonts.Arial12, Muted);
            Text(batch, best == null ? "--" : Eta(best.Turns), X + Width * .89f, Y + 20, Width * .11f, Fonts.Arial12Bold, Colors.Cream);
            if (Hovered)
                ToolTip.CreateTooltip($"{Entry.Building.TranslatedName.Text}\n{Entry.Building.DescriptionText.Text}\n"
                    + $"{Entry.Built} completed in your empire; {Entry.Queued} in construction.\n"
                    + (best?.Reason ?? "No owned colony meets the building and tile requirements.")
                    + "\nDouble-click to queue one copy automatically.");
            base.Draw(batch, elapsed);
        }
    }

    sealed class ActionButton : UIElementV2
    {
        public string Caption;
        readonly Action Click;
        readonly Func<bool> Selected;
        bool Hover;
        public ActionButton(RectF rect, string caption, Action click, Func<bool> selected = null) : base(rect)
        {
            Caption = caption;
            Click = click;
            Selected = selected;
        }
        public override bool HandleInput(InputState input)
        {
            Hover = Rect.HitTest(input.CursorPosition);
            if (!Hover || !Enabled) return false;
            if (input.LeftMouseClick) { GameAudio.AcceptClick(); Click(); }
            return true;
        }
        public override void Draw(SpriteBatch batch, DrawTimes elapsed)
        {
            bool active = Enabled && (Hover || Selected?.Invoke() == true);
            DrawPanel(batch, RectF, active);
            float textWidth = Fonts.Arial12Bold.TextWidth(Caption);
            Text(batch, Caption, X + Math.Max(6, (Width - textWidth) / 2), Y + (Height - Fonts.Arial12Bold.LineSpacing) / 2,
                Width - 12, Fonts.Arial12Bold, !Enabled ? Muted : active ? Cyan : Gold);
        }
    }
}
