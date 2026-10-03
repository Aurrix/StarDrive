using System;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using SDGraphics;
using Ship_Game.Ships;
using Color = Microsoft.Xna.Framework.Color;

namespace Ship_Game;

public sealed partial class EmpireAssetsPanel
{
    static readonly Color Accent = new(190, 159, 101);
    static readonly Color Muted = new(174, 173, 155);
    static readonly Color Edge = new(68, 66, 52);
    SubTexture FrameRail, FrameBody, FrameEnd;
    FrameStrip RailStrip, BodyStrip, EndStrip;

    // Preserve end caps and repeat the middle at its original display scale.
    sealed class FrameStrip
    {
        readonly SubTexture Top, Middle, Bottom;
        const float CapHeight = 64;
        const float MiddleHeight = 624 - CapHeight * 2;

        public FrameStrip(SubTexture source)
        {
            int cap = Math.Max(1, (int)Math.Round(source.Height * CapHeight / 624));
            SubTexture Part(int y, int height) => new(source.Name, source.X, source.Y + y,
                source.Width, height, source.Texture, source.TexturePath);
            Top = Part(0, cap);
            Middle = Part(cap, source.Height - cap * 2);
            Bottom = Part(source.Height - cap, cap);
        }

        public void Draw(SpriteBatch batch, RectF rect)
        {
            float cap = Math.Min(CapHeight, rect.H / 2);
            batch.Draw(Top, new RectF(rect.X, rect.Y, rect.W, cap), Color.White);
            batch.Draw(Bottom, new RectF(rect.X, rect.Bottom - cap, rect.W, cap), Color.White);
            float bottom = rect.Bottom - cap;
            for (float y = rect.Y + cap; y < bottom; y += MiddleHeight)
            {
                float height = Math.Min(MiddleHeight, bottom - y);
                int sourceHeight = Math.Max(1, (int)Math.Round(Middle.Height * height / MiddleHeight));
                var cropped = new SubTexture(Middle.Name, Middle.X, Middle.Y, Middle.Width,
                    sourceHeight, Middle.Texture, Middle.TexturePath);
                batch.Draw(cropped, new RectF(rect.X, y, rect.W, height), Color.White);
            }
        }
    }

    void DrawFrame(SpriteBatch batch)
    {
        if (FrameRail == null)
        {
            SubTexture source = ResourceManager.Texture("NewUI/automation_frame_v1");
            int split = (int)(source.Width * 0.22f);
            int end = (int)(source.Width * 0.92f);
            FrameRail = new SubTexture(source.Name, source.X, source.Y, split, source.Height, source.Texture, source.TexturePath);
            FrameBody = new SubTexture(source.Name, source.X + split, source.Y, source.Width - split, source.Height, source.Texture, source.TexturePath);
            FrameEnd = new SubTexture(source.Name, source.X + end, source.Y, source.Width - end, source.Height, source.Texture, source.TexturePath);
            RailStrip = new FrameStrip(FrameRail);
            BodyStrip = new FrameStrip(FrameBody);
            EndStrip = new FrameStrip(FrameEnd);
        }
        // The rail uses identical artwork, position and scale in both states.
        RectF frame = FrameRect;
        RailStrip.Draw(batch, new RectF(frame.X, frame.Y, RailWidth + 28, frame.H));
        (Collapsed ? EndStrip : BodyStrip).Draw(batch,
            new RectF(ContentX, frame.Y, frame.Right - ContentX, frame.H));
        // Opaque charcoal keeps nebulae and the decorative frame out of the glyphs.
        batch.FillRectangle(new RectF(X, Y - 5, RailWidth - 8, Height + 5), new Color(10, 14, 19));
        batch.DrawRectangle(new RectF(X, Y - 5, RailWidth - 8, Height + 5), new Color(48, 57, 67));
    }

    static void Text(SpriteBatch batch, string text, float x, float y, Color color, bool bold = false)
        => batch.DrawString(bold ? Fonts.Arial12Bold : Fonts.Arial10, text, new Vector2(x, y), color);

    static string Fit(string text, float width, bool bold = false)
    {
        text ??= "";
        var font = bold ? Fonts.Arial12Bold : Fonts.Arial10;
        if (font.TextWidth(text) <= width) return text;
        while (text.Length > 0 && font.TextWidth(text + "...") > width)
            text = text.Substring(0, text.Length - 1);
        return text + "...";
    }

    static string Compact(float value)
    {
        float absolute = Math.Abs(value);
        if (absolute >= 1000000) return $"{value / 1000000:0.#}M";
        if (absolute >= 1000) return $"{value / 1000:0.#}K";
        return $"{value:0.#}";
    }

    static void Texture(SpriteBatch batch, string name, RectF rect, Color color)
        => batch.Draw(ResourceManager.Texture(name), rect, color);

    void DrawActionRail(SpriteBatch batch)
    {
        foreach (var (action, button) in NavigationButtons())
        {
            bool hover = button.HitTest(Cursor);
            bool active = action == SidebarAction.Assets ? !Collapsed && !AutomationSelected
                : action == SidebarAction.Automation ? !Collapsed && AutomationSelected
                : action == SidebarAction.Construction && Screen.DeepSpaceBuildWindow.Visible;
            Color trim = hover ? Color.Wheat : active ? Accent : new Color(65, 77, 90);
            batch.FillRectangle(button, hover ? new Color(44, 55, 68)
                : active ? new Color(57, 48, 30) : new Color(18, 25, 34));
            batch.DrawRectangle(button, trim);
            DrawNavigationGlyph(batch, action, button, active || hover ? Color.Wheat : new Color(235, 242, 250));
            if (active)
                batch.FillRectangle(new RectF(button.X, button.Y + 5, 2, button.H - 10), Accent);
            if (hover) ToolTip.CreateTooltip(NavigationTips[(int)action]);
        }
        if (NavigationMaxOffset > 0)
        {
            Text(batch, "^", NavigationUpRect.X + 12, NavigationUpRect.Y - 2, NavigationOffset > 0 ? Accent : Edge);
            Text(batch, "v", NavigationDownRect.X + 12, NavigationDownRect.Y, NavigationOffset < NavigationMaxOffset ? Accent : Edge);
            if (NavigationScrollRect.HitTest(Cursor))
            {
                if (NavigationUpRect.HitTest(Cursor) || NavigationDownRect.HitTest(Cursor))
                    ToolTip.CreateTooltip("Scroll sidebar shortcuts");
            }
        }
    }

    // Bold, untextured symbols on a 32px grid remain legible at the rail's native size.
    static void DrawNavigationGlyph(SpriteBatch batch, SidebarAction action, RectF button, Color color)
    {
        Vector2 P(float x, float y) => new(button.X + x, button.Y + y);
        void L(float x, float y, float x2, float y2) => batch.DrawLine(P(x, y), P(x2, y2), color, 2);
        void R(float x, float y, float w, float h) => batch.DrawRectangle(new RectF(button.X + x, button.Y + y, w, h), color, 2);
        void F(float x, float y, float w, float h) => batch.FillRectangle(new RectF(button.X + x, button.Y + y, w, h), color);
        void C(float x, float y, float radius) => batch.DrawCircle(P(x, y), radius, 20, color, 2);
        void Ship(float x, float y, float size)
        {
            L(x, y, x - size, y + size * 2);
            L(x - size, y + size * 2, x, y + size * 1.5f);
            L(x, y + size * 1.5f, x + size, y + size * 2);
            L(x + size, y + size * 2, x, y);
        }
        switch (action)
        {
            case SidebarAction.Assets: // Four inventory tiles
                R(6, 6, 8, 8); R(19, 6, 8, 8); R(6, 19, 8, 8); R(19, 19, 8, 8);
                break;
            case SidebarAction.Automation: // Gear
                C(16, 16, 7); C(16, 16, 2);
                for (int i = 0; i < 8; ++i)
                {
                    float angle = i * MathF.PI / 4;
                    float x = MathF.Cos(angle), y = MathF.Sin(angle);
                    L(16 + x * 8, 16 + y * 8, 16 + x * 11, 16 + y * 11);
                }
                break;
            case SidebarAction.Colonies: // Ringed planet
                C(16, 16, 8); L(4, 22, 28, 10); L(4, 22, 5, 17); L(28, 10, 27, 15);
                break;
            case SidebarAction.Research: // Laboratory flask
                L(12, 5, 20, 5); L(13, 5, 13, 13); L(19, 5, 19, 13);
                L(13, 13, 6, 26); L(19, 13, 26, 26); L(6, 26, 26, 26); L(10, 20, 22, 20);
                break;
            case SidebarAction.Economy: // Rising bar chart
                F(6, 19, 4, 7); F(14, 13, 4, 13); F(22, 6, 4, 20); L(5, 27, 28, 27);
                break;
            case SidebarAction.Empire: // Flag
                L(8, 5, 8, 27); L(8, 6, 25, 6); L(25, 6, 21, 12);
                L(21, 12, 25, 17); L(25, 17, 8, 17);
                break;
            case SidebarAction.Diplomacy: // Conversation bubbles
                R(5, 6, 16, 12); L(9, 18, 9, 22); L(9, 22, 14, 18);
                L(24, 11, 27, 11); L(27, 11, 27, 23); L(27, 23, 23, 23);
                L(23, 23, 23, 27); L(23, 27, 18, 23); L(18, 23, 15, 23);
                break;
            case SidebarAction.Fleets:
                Ship(16, 5, 4); Ship(8, 17, 4); Ship(24, 17, 4);
                break;
            case SidebarAction.Espionage: // Watching eye
                L(4, 16, 11, 9); L(11, 9, 21, 9); L(21, 9, 28, 16);
                L(28, 16, 21, 23); L(21, 23, 11, 23); L(11, 23, 4, 16); C(16, 16, 4);
                break;
            case SidebarAction.Shipyard: // Ship in a dock
                Ship(16, 6, 5); L(5, 13, 5, 26); L(5, 26, 27, 26); L(27, 26, 27, 13);
                break;
            case SidebarAction.Ships:
                Ship(16, 5, 10); L(13, 25, 13, 28); L(19, 25, 19, 28);
                break;
            case SidebarAction.Blueprints: // Plan sheet
                R(7, 5, 19, 23); L(11, 11, 22, 11); L(11, 11, 11, 23);
                L(11, 18, 22, 18); L(18, 11, 18, 23);
                break;
            case SidebarAction.Construction: // Hammer
                L(9, 25, 21, 13); L(6, 23, 18, 11); L(6, 23, 9, 26);
                L(13, 7, 19, 5); L(19, 5, 27, 13); L(27, 13, 23, 17); L(23, 17, 13, 7);
                break;
            case SidebarAction.Help: // Question mark
                L(10, 10, 13, 6); L(13, 6, 20, 6); L(20, 6, 24, 10);
                L(24, 10, 24, 14); L(24, 14, 16, 19); L(16, 19, 16, 22); F(15, 26, 3, 3);
                break;
        }
    }

    static void DrawChevron(SpriteBatch batch, RectF rect, bool pointsRight, Color color)
    {
        float x = rect.X + rect.W / 2;
        float y = rect.Y + rect.H / 2;
        float direction = pointsRight ? 1 : -1;
        batch.DrawLine(new Vector2(x - direction * 3, y - 5), new Vector2(x + direction * 3, y), color);
        batch.DrawLine(new Vector2(x + direction * 3, y), new Vector2(x - direction * 3, y + 5), color);
    }

    void DrawAssetsHeader(SpriteBatch batch)
    {
        RectF r = HeaderRect;
        bool hover = r.HitTest(Cursor);
        Color trim = hover ? Color.Wheat : Accent;
        // Draw the title and controls directly over the textured housing.

        string count = Counts[0].ToString();
        float countWidth = Math.Max(25, Fonts.Arial10.TextWidth(count) + 12);
        var toggle = new RectF(r.Right - 27, r.Y + 5, 21, 18);
        var badge = new RectF(toggle.X - countWidth - 5, r.Y + 6, countWidth, 16);
        Text(batch, Fit("EMPIRE ASSETS", badge.X - r.X - 12, bold: true), r.X + 7, r.Y + 7, Color.Wheat, true);
        batch.FillRectangle(badge, new Color(12, 14, 12));
        batch.DrawRectangle(badge, Edge);
        Text(batch, count, badge.X + (badge.W - Fonts.Arial10.TextWidth(count)) / 2, badge.Y + 1, trim);
        batch.FillRectangle(toggle, hover ? new Color(66, 57, 36) : new Color(33, 34, 27));
        batch.DrawRectangle(toggle, trim);
        DrawChevron(batch, toggle, pointsRight: false, trim);
        if (hover) ToolTip.CreateTooltip("Collapse Empire Assets to the left");
    }

    public override void Draw(SpriteBatch batch, DrawTimes elapsed)
    {
        if (ShouldHide) return;
        DrawFrame(batch);
        DrawActionRail(batch);
        if (Collapsed)
        {
            return;
        }
        if (AutomationSelected)
        {
            Screen.aw.DrawSidebarHeader(batch, new RectF(HeaderRect.X + 7, HeaderRect.Y, HeaderRect.W - 14, HeaderRect.H));
            Screen.aw.DrawSidebar(batch, AutomationRect, Cursor);
            return;
        }
        DrawAssetsHeader(batch);
        var searchRect = new RectF(ContentX + 8, Y + 31, ContentWidth - 16, 26);
        batch.FillRectangle(searchRect, new Color(20, 21, 17));
        batch.DrawRectangle(searchRect, Edge);
        if (Search.Text.Length == 0 && !Search.HandlingInput)
            Text(batch, "Search assets or systems...", ContentX + 12, Y + 37, Muted);
        for (int i = 0; i < CategoryNames.Length; ++i)
        {
            RectF tab = TabRect(i);
            bool selected = (int)Filter == i;
            string state = selected ? "_pressed" : tab.HitTest(Cursor) ? "_hover" : "";
            Texture(batch, "EmpireTopBar/empiretopbar_btn_68px" + state, tab, Color.White);
            Texture(batch, CategoryIcons[i], new RectF(tab.X + (tab.W - 18) / 2, tab.Y + 4, 18, 18),
                    selected ? Color.Wheat : Color.White);
            if (selected) batch.FillRectangle(new RectF(tab.X + 7, tab.Bottom - 3, tab.W - 14, 1), Accent);
            if (tab.HitTest(Cursor)) ToolTip.CreateTooltip($"{CategoryNames[i]} ({Counts[i]})");
        }
        foreach (var (row, rect) in VisibleRows()) DrawRow(batch, row, rect);
        if (MaxOffset > 0)
        {
            var track = new RectF(ListRect.Right - 4, ListRect.Y, 3, ListRect.H);
            batch.FillRectangle(track, Edge);
            float totalHeight = 0;
            foreach (Row row in Rows) totalHeight += row.Height;
            float thumbHeight = Math.Max(16, track.H * track.H / totalHeight);
            batch.FillRectangle(new RectF(track.X, track.Y + (track.H - thumbHeight) * Offset / MaxOffset,
                                          3, thumbHeight), Accent);
        }
        Text(batch, "Sort", FooterRect.X, FooterRect.Y + 3, Muted);
        for (int i = 0; i < SortNames.Length; ++i)
        {
            RectF button = SortRect(i);
            batch.FillRectangle(button, (int)SortBy == i ? new Color(48, 45, 31) : new Color(19, 21, 18));
            batch.DrawRectangle(button, (int)SortBy == i ? Accent : Edge);
            if (i == 0) Text(batch, "Aa", button.X + 6, button.Y + 3, Color.Wheat);
            else Texture(batch, SortIcons[i], new RectF(button.X + 7, button.Y + 3, 14, 14), Color.White);
            if (button.HitTest(Cursor)) ToolTip.CreateTooltip(i == 0
                ? "Sort by name. Click again to reverse."
                : $"Sort planets by net {SortNames[i].ToLowerInvariant()} per turn, highest first. Click again for lowest first.");
        }
        Text(batch, SortBy == AssetSort.Name ? (ReverseSort ? "Z-A" : "A-Z")
            : ReverseSort ? "High to low" : "Low to high", SortDirectionRect.X + 4, FooterRect.Y + 3, Accent);
        if (SortDirectionRect.HitTest(Cursor)) ToolTip.CreateTooltip("Reverse sort direction");
        base.Draw(batch, elapsed);
    }

    void DrawRow(SpriteBatch batch, Row row, RectF rect)
    {
        if (row.IsHeader)
        {
            Texture(batch, "NewUI/submenu_header_middle", rect, Color.White);
            Text(batch, $"{(SectionCollapsed[(int)row.Kind] ? "+" : "-")}  {row.Name} ({Counts[(int)row.Kind]})",
                 rect.X + 6, rect.Y + 2, Color.Wheat);
            return;
        }
        bool selected = row.Fleet != null ? Screen.SelectedFleet == row.Fleet
                      : row.Planet != null ? Screen.SelectedPlanet == row.Planet : Screen.SelectedShip == row.Station;
        bool hover = rect.HitTest(Cursor);
        batch.FillRectangle(rect, selected ? new Color(48, 45, 31) : hover ? new Color(34, 35, 28) : new Color(19, 21, 18));
        batch.DrawRectangle(rect, selected ? Accent : Edge);
        Color stripe = row.Fleet != null && row.Fleet.Ships.Any(ship => !ship.OnLowAlert) ? Color.IndianRed : Accent;
        batch.FillRectangle(new RectF(rect.X, rect.Y + 2, 2, rect.H - 4), stripe);
        var iconRect = new RectF(rect.X + 5, rect.Y + 5, 28, 28);
        if (row.Station != null) row.Station.TacticalIcon().Draw(batch, iconRect, Screen.Player.EmpireColor);
        else batch.Draw(row.Fleet?.Icon ?? ResourceManager.Texture(row.Planet.IconPath), iconRect,
                        row.Fleet != null ? Screen.Player.EmpireColor : Color.White);
        float textX = rect.X + 39;
        string strength = row.Planet == null ? "STR " + Compact(row.Strength) : "";
        float strengthWidth = row.Planet == null ? Fonts.Arial10.TextWidth(strength) + 10 : 0;
        string rowName = row.Name;
        if (row.Planet != null && Screen.UState.PlanetHotkeyIds != null)
        {
            int key = Array.IndexOf(Screen.UState.PlanetHotkeyIds, row.Planet.Id) + 1;
            if (key > 0) rowName = $"[{(key > 10 ? "Alt+" : "")}{key % 10}] {rowName}";
        }
        Text(batch, Fit(rowName, rect.W - 65 - strengthWidth, bold: true), textX, rect.Y + 2, Color.Wheat, true);
        if (row.Planet == null)
            Text(batch, strength, rect.Right - 27 - strengthWidth + 10, rect.Y + 4, Accent);
        RectF centerRect = CenterRect(rect);
        Text(batch, ">", centerRect.X + 6, centerRect.Y, centerRect.HitTest(Cursor) ? Color.White : Accent, true);

        string tooltip = $"{row.Name}\nClick to select. Double-click or > to center the camera.";
        if (row.Planet != null)
        {
            DrawPlanet(batch, row, rect);
            tooltip = $"{row.Name} ({row.Planet.System.Name})\nPopulation: {row.Planet.PopulationStringForPlayer} billion\nNet food: {row.Planet.Food.NetIncome:0.#} / production: {row.Planet.Prod.NetIncome:0.#} / research: {row.Planet.Res.NetIncome:0.#}\nClick to select. > centers the system. Double-click opens the colony.";
            if (QueueRect(rect).HitTest(Cursor)) tooltip = QueueTooltip(row);
            else tooltip += "\nCtrl+number: assign hotkey. Number: select. Double-tap: open colony.\nAssigning ships to the same key restores its fleet shortcut.";
            if (RushRect(rect).HitTest(Cursor))
                tooltip = row.Queue.Length == 0 ? "Nothing queued to rush."
                    : $"Rush: {row.Queue[0].DisplayText}\nClick: spend up to 10 stored production.\nCtrl-click: use available stored production.\nShift-click: toggle continuous rush.\nStored production: {row.Planet.ProdHere:0.#}. Standard rush fees apply.";
        }
        else if (row.Fleet != null)
        {
            DrawComposition(batch, row, new RectF(textX, rect.Y + 22, rect.W - 46, 16));
            if (hover)
            {
                var description = new StringBuilder($"{row.Name}: {row.Members.Length} ships, strength {row.Strength:0}\n");
                foreach (var group in row.Composition)
                    description.AppendLine($"{group.Count} x {group.Icon.Primary.Name.Replace("symbol_", "").Replace('_', ' ')}");
                description.Append("Click to select. Double-click or > to center the camera.");
                tooltip = description.ToString();
            }
        }
        else
        {
            string location = row.Station.GetTether()?.Name ?? row.Station.System?.Name ?? "Deep space";
            Text(batch, Fit(location, rect.W - 48), textX, rect.Y + 20, Muted);
            tooltip = $"{row.Name}\n{CategoryNames[(int)row.Kind]} - {location}\nStrength: {row.Strength:0}\nClick to select. Double-click or > to center the camera.";
        }
        if (hover) ToolTip.CreateTooltip(tooltip);
    }

    void DrawPlanet(SpriteBatch batch, Row row, RectF rect)
    {
        Planet planet = row.Planet;
        float x = rect.X + 39, statWidth = (rect.W - 43) / 4;
        void Stat(int index, string icon, string value, Color color)
        {
            float sx = x + index * statWidth;
            Texture(batch, "NewUI/" + icon, new RectF(sx, rect.Y + 21, 12, 12), Color.White);
            Text(batch, Fit(value, statWidth - 16), sx + 15, rect.Y + 19, color);
        }
        Stat(0, "icon_population", $"{planet.PopulationBillion:0.#}B", Muted);
        Stat(1, "icon_food", Compact(planet.Food.NetIncome), planet.Food.NetIncome < 0 ? Color.Salmon : Muted);
        Stat(2, "icon_production", Compact(planet.Prod.NetIncome), planet.Prod.NetIncome < 0 ? Color.Salmon : Muted);
        Stat(3, "icon_science", Compact(planet.Res.NetIncome), Muted);

        RectF queueRect = QueueRect(rect), rushRect = RushRect(rect);
        if (row.Queue.Length == 0)
        {
            Text(batch, "No construction", queueRect.X, queueRect.Y, Muted);
            Texture(batch, "NewUI/icon_queue_rushconstruction", rushRect, Color.DimGray);
            return;
        }
        QueueItem item = row.Queue[0];
        DrawQueueIcon(batch, item, new RectF(queueRect.X, queueRect.Y, 13, 13));
        string progress = $"{Math.Clamp(item.ProductionSpent / Math.Max(1, item.ActualCost), 0, 1):P0}";
        float progressWidth = Fonts.Arial10.TextWidth(progress);
        // First item + two upcoming thumbnails; remaining items stay visible as a queue count.
        int next = Math.Min(2, row.Queue.Length - 1);
        float tailWidth = next * 16 + (row.Queue.Length > 3 ? 27 : 0);
        float textWidth = queueRect.W - 20 - progressWidth - tailWidth;
        Text(batch, Fit(item.DisplayText, textWidth), queueRect.X + 17, queueRect.Y, item.Rush ? Color.Gold : Muted);
        float end = queueRect.Right - tailWidth;
        Text(batch, progress, end - progressWidth, queueRect.Y, Accent);
        for (int i = 0; i < next; ++i)
            DrawQueueIcon(batch, row.Queue[i + 1], new RectF(end + 3 + i * 16, queueRect.Y, 13, 13));
        if (row.Queue.Length > 3) Text(batch, $"+{row.Queue.Length - 3}", queueRect.Right - 24, queueRect.Y, Muted);
        batch.FillRectangle(new RectF(queueRect.X, rect.Bottom - 2, queueRect.W, 1), Edge);
        batch.FillRectangle(new RectF(queueRect.X, rect.Bottom - 2,
            queueRect.W * Math.Clamp(item.ProductionSpent / Math.Max(1, item.ActualCost), 0, 1), 1), Accent);
        string rushSuffix = rushRect.HitTest(Cursor) ? "_hover2" : "";
        Texture(batch, "NewUI/icon_queue_rushconstruction" + rushSuffix, rushRect, item.Rush ? Color.Gold : Color.White);
    }

    static void DrawQueueIcon(SpriteBatch batch, QueueItem item, RectF rect)
    {
        SubTexture icon = item.isBuilding ? item.Building.IconTex : item.ShipData?.Icon;
        if (icon != null) batch.Draw(icon, rect, Color.White);
        else Texture(batch, item.isTroop ? "TacticalIcons/symbol_troop" : "NewUI/icon_production", rect, Color.White);
    }

    static string QueueTooltip(Row row)
    {
        if (row.Queue.Length == 0) return "Construction queue is empty.";
        var text = new StringBuilder("Construction queue\n");
        for (int i = 0; i < Math.Min(row.Queue.Length, 8); ++i)
        {
            QueueItem item = row.Queue[i];
            text.AppendLine($"{i + 1}. {item.DisplayText} ({item.ProductionSpent:0}/{item.ActualCost:0}){(item.Rush ? " - continuous rush" : "")}");
        }
        if (row.Queue.Length > 8) text.Append($"+{row.Queue.Length - 8} more items");
        return text.ToString();
    }

    void DrawComposition(SpriteBatch batch, Row row, RectF rect)
    {
        if (row.Members.Length <= (int)(rect.W / 17))
        {
            for (int i = 0; i < row.Members.Length; ++i)
            {
                Ship ship = row.Members[i];
                var iconRect = new RectF(rect.X + i * 17, rect.Y, 15, 15);
                Color status = ship.GetStatusColor();
                if (status != Color.Black)
                    Texture(batch, "TacticalIcons/symbol_status", iconRect, Screen.ApplyCurrentAlphaToColor(status));
                ship.TacticalIcon().Draw(batch, iconRect, ship.Resupplying ? Color.Gray : Screen.Player.EmpireColor);
            }
            return;
        }
        float x = rect.X;
        int remaining = row.Members.Length;
        foreach (var group in row.Composition)
        {
            string count = $"{group.Count}x";
            float width = Fonts.Arial10.TextWidth(count) + 23;
            if (x + width > rect.Right - 35) break;
            Text(batch, count, x, rect.Y - 1, Muted);
            group.Icon.Draw(batch, new RectF(x + width - 20, rect.Y, 15, 15), Screen.Player.EmpireColor);
            x += width;
            remaining -= group.Count;
        }
        if (remaining > 0) Text(batch, $"+{remaining}", x, rect.Y - 1, Accent);
    }
}
