using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TrickyMaddnessLevelHook
{
    // Level select as a vertical scrolling GRID with a heading per source game,
    // replacing the vanilla one-row horizontal strip. Lives on the "Scroll View"
    // GameObject, same as LevelSelectScroller (which it replaces when enabled).
    //
    // Why: the strip was fine at 4 built-in levels and stayed usable at a handful
    // of mods. At 79 custom maps a 640-wide card in a 3840-wide canvas means 6 on
    // screen and ~14 screens of horizontal scrolling, with nothing telling you
    // which game a course came from. A grid at the same card size shows ~20 at a
    // time in ~4 screens, and the headings make the eras findable.
    //
    // ⚠️ THE constraint (IL-verified in LevelSelectMenu::OnEnable): the menu selects
    // its start card with content.GetChild(Mathf.Clamp(lastSelectedLevel + 1, 1,
    // entryCount)).GetComponentInChildren<Button>().gameObject — no null check. So
    // Content's children 1..N must stay exactly the N cards, in entry order. Era
    // dividers are therefore APPENDED after the last card (index > N), never
    // interleaved; landing the clamp on a divider is a NullReferenceException out
    // of OnEnable that leaves the menu with nothing selected. They are positioned
    // manually, so sibling order and visual order are unrelated.
    //
    // ⚠️ Card sizeDelta belongs to LevelSelectEntry::FixedUpdate, which springs it
    // 640x360 -> 768x432 (and nameLabel.fontSize 40 -> 48) on the selected card
    // every fixed step. We own anchoredPosition ONLY. That is why the cell pitch is
    // the SELECTED size: a cell exactly contains a card at full growth, so the
    // selected card never overlaps its neighbours.
    public class LevelSelectGrid : MonoBehaviour
    {
        private const int Columns = 5;
        // == LevelSelectEntry's selected-card size; see the note above.
        private const float CellW = 768f;
        private const float CellH = 432f;
        private const float GridW = Columns * CellW; // 3840 == the canvas reference width
        // The nav bar (BottomBar / NavOptions) occupies y 0..216 of the 2160-tall
        // reference canvas and the Scroll View is anchored at 0.1 with a bottom
        // pivot, so the grid grows upward from y=216 and 1944 is full height.
        private const float MaxGridH = 1944f;
        private const float HeaderH = 140f;
        private const float HeaderInset = 48f;
        private const float TopPad = 30f;
        private const float SectionGap = 40f;
        private const float BottomPad = 60f;
        private const float EdgeMargin = 24f;

        private ScrollRect scrollRect;
        private RectTransform content, viewport, scrollView;
        private bool built;
        private float contentHeight;

        // Parallel to `cards`: each card's cell top/bottom as a distance DOWN from
        // the content's top edge, which is the same space content.anchoredPosition.y
        // scrolls in. cardTop for a card on a section's first row includes the
        // heading above it, so selecting into a new section scrolls the heading
        // into view with it.
        private readonly List<RectTransform> cards = new List<RectTransform>();
        private readonly List<float> cardTop = new List<float>();
        private readonly List<float> cardBottom = new List<float>();
        private readonly List<GameObject> dividers = new List<GameObject>();

        private TextMeshProUGUI labelTemplate;

        public void Init(ScrollRect sr) { scrollRect = sr; }

        private void OnEnable()
        {
            // AddComponent fires OnEnable before Init runs, but we're on the
            // ScrollRect's own GameObject, so GetComponent always resolves it.
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (!built) Build();
        }

        private void Build()
        {
            if (scrollRect == null) return;
            content = scrollRect.content;
            viewport = scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.transform.GetChild(0) as RectTransform;
            scrollView = transform as RectTransform;
            if (content == null || viewport == null || scrollView == null)
            {
                Plugin.Log.LogWarning("[LevelSelect] grid: ScrollRect has no content/viewport; leaving the strip alone");
                return;
            }

            // Drop any dividers from a previous build before recounting cards.
            for (int i = 0; i < dividers.Count; i++)
                if (dividers[i] != null) Destroy(dividers[i]);
            dividers.Clear();

            // Content child 0 is the inactive card template; everything active with
            // a Selectable on it is a card. (The Selectable test also means a
            // divider surviving from an earlier build can never be counted as one.)
            cards.Clear(); cardTop.Clear(); cardBottom.Clear();
            for (int i = 0; i < content.childCount; i++)
            {
                var ch = content.GetChild(i) as RectTransform;
                if (ch == null || !ch.gameObject.activeSelf) continue;
                if (ch.GetComponent<Selectable>() == null) continue;
                cards.Add(ch);
            }
            if (cards.Count == 0)
            {
                Plugin.Log.LogWarning("[LevelSelect] grid: no cards found under Content; leaving the strip alone");
                return;
            }

            // Split into contiguous era runs BEFORE touching anything, so the
            // nothing-to-group case below can still bail with the menu untouched.
            var sectionGroup = new List<string>();
            var sectionStart = new List<int>();
            for (int i = 0; i < cards.Count; )
            {
                string group = LevelSelectMenu_Populate.GroupOfCard(i);
                sectionGroup.Add(group);
                sectionStart.Add(i);
                while (i < cards.Count && LevelSelectMenu_Populate.GroupOfCard(i) == group) i++;
            }

            // Nothing to group — one era, one row's worth of cards. That is exactly
            // the empty-Maps-folder case: the game's own four levels and nothing
            // else. A grid there is strictly worse than vanilla (four cards pinned
            // to the top-left of a screen-tall empty panel, under a heading naming
            // the only section there is), and headings exist to separate things
            // there is nothing to separate. Leave the menu pixel-identical to
            // vanilla, same as LevelSelectScroller does when everything fits.
            if (sectionGroup.Count <= 1 && cards.Count <= Columns)
            {
                Plugin.Log.LogInfo($"[LevelSelect] grid: only {cards.Count} level(s) in one section — "
                    + "leaving the vanilla strip alone (nothing to group).");
                return;
            }

            labelTemplate = cards[0].GetComponentInChildren<TextMeshProUGUI>(true);
            if (labelTemplate == null)
                Plugin.Log.LogWarning("[LevelSelect] grid: no TMP label on the card template — "
                    + "section headings will draw as rules with no text.");

            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // The scene's layout components fight manual placement. Neither is read
            // by any game code (verified over the whole Assembly-CSharp dump), and
            // the ContentSizeFitter is Unconstrained on both axes in the scene
            // anyway — it only ever did anything because LevelSelectScroller turned
            // it on. Disable rather than destroy so the strip fallback still finds them.
            var hlg = content.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null) hlg.enabled = false;
            var csf = content.GetComponent<ContentSizeFitter>();
            if (csf != null) csf.enabled = false;

            // Un-stretch Content and pin its top-left to the viewport's, so every
            // card/divider position below is an absolute offset from that corner.
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 1f);
            content.anchoredPosition = Vector2.zero;

            var rows = new List<List<Selectable>>();
            float y = TopPad;
            for (int s = 0; s < sectionGroup.Count; s++)
            {
                int idx = sectionStart[s];
                int end = s + 1 < sectionStart.Count ? sectionStart[s + 1] : cards.Count;

                float headerTop = y;
                CreateDivider(sectionGroup[s], headerTop);
                y += HeaderH;

                int n = end - idx;
                int rowsInSection = (n + Columns - 1) / Columns;
                // A section too small to fill one row is centred rather than left
                // in the corner — a lone SSX 3 map under a full row of Tricky ones
                // reads as a layout bug otherwise. Multi-row sections stay left
                // aligned so their columns line up with the section above.
                float indent = rowsInSection == 1 ? (Columns - n) * CellW * 0.5f : 0f;
                int firstRow = rows.Count;
                for (int k = 0; k < n; k++)
                {
                    int r = k / Columns, c = k % Columns;
                    float top = y + r * CellH;
                    var rt = cards[idx + k];
                    rt.anchoredPosition = new Vector2(
                        indent + c * CellW + CellW * 0.5f,
                        -(top + CellH * 0.5f));
                    cardTop.Add(r == 0 ? headerTop : top);
                    cardBottom.Add(top + CellH);
                    while (rows.Count <= firstRow + r) rows.Add(new List<Selectable>());
                    rows[firstRow + r].Add(rt.GetComponent<Selectable>());
                }

                y += rowsInSection * CellH + SectionGap;
            }
            contentHeight = y - SectionGap + BottomPad;
            content.sizeDelta = new Vector2(GridW, contentHeight);

            // Grow the strip into a panel — but never taller than what it holds, so
            // a two-section install doesn't get a screen of empty space under it.
            // Only sizeDelta changes: the Scroll View's anchors/pivot stay put (it
            // is pivoted at its bottom edge, so this grows upward off the nav bar),
            // and its Animation component (a 0.1 s anchoredPosition.x slide-in whose
            // clip is SHARED with character select — do not edit it) is left running
            // and untouched.
            float gridH = Mathf.Clamp(
                Plugin.levelSelectGridHeight != null ? Plugin.levelSelectGridHeight.Value : 1800f,
                CellH, MaxGridH);
            gridH = Mathf.Min(gridH, contentHeight);
            scrollView.sizeDelta = new Vector2(GridW, gridH);

            WireNavigation(rows);

            built = true;
            Plugin.Log.LogInfo($"[LevelSelect] grid: {cards.Count} card(s) in {dividers.Count} section(s), "
                + $"{rows.Count} row(s), content {contentHeight:0} px tall in a {gridH:0} px viewport");
        }

        private void CreateDivider(string label, float top)
        {
            var go = new GameObject("SectionDivider_" + label, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(content, false); // appends — never insert, see the class note
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(GridW, HeaderH);
            rt.anchoredPosition = new Vector2(0f, -top);
            dividers.Add(go);

            if (labelTemplate != null)
            {
                var lbl = Instantiate(labelTemplate.gameObject, rt);
                lbl.name = "Label";
                lbl.SetActive(true);
                // A heading must never be focusable: it would be a dead end for
                // navigation and a NullReference risk if anything indexed it.
                foreach (var s in lbl.GetComponentsInChildren<Selectable>(true)) Destroy(s);
                var lrt = (RectTransform)lbl.transform;
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.pivot = new Vector2(0.5f, 0.5f);
                lrt.offsetMin = new Vector2(HeaderInset, 0f);
                lrt.offsetMax = new Vector2(-HeaderInset, 0f);
                var tmp = lbl.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.enableAutoSizing = false;
                    tmp.fontSize = 56f;
                    tmp.alignment = TextAlignmentOptions.MidlineLeft;
                    tmp.enableWordWrapping = false;
                    tmp.raycastTarget = false;
                    tmp.text = label;
                }
            }

            // Rule under the heading, so a section reads as a band even where the
            // cards below it are still thumbnail-less grey boxes.
            var line = new GameObject("Rule", typeof(RectTransform), typeof(Image));
            var lineRt = (RectTransform)line.transform;
            lineRt.SetParent(rt, false);
            lineRt.anchorMin = new Vector2(0f, 0f);
            lineRt.anchorMax = new Vector2(1f, 0f);
            lineRt.pivot = new Vector2(0.5f, 0f);
            lineRt.sizeDelta = new Vector2(-2f * HeaderInset, 4f);
            lineRt.anchoredPosition = new Vector2(0f, 14f);
            var img = line.GetComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.25f);
            img.raycastTarget = false;
        }

        // Explicit four-way navigation on EVERY card, computed from its grid slot.
        //
        // Two reasons this isn't left to the engine. (1) LevelSelectMenu::Populate
        // rewrites the FIRST and LAST card with a zeroed Navigation set to Explicit
        // and only selectOnLeft/selectOnRight filled — up and down are null, so in a
        // grid those two cards are dead ends. (2) Every other card is authored at
        // mode -1, which Selectable treats bitwise as automatic on both axes; that
        // works, but its neighbour search is geometric and can pick diagonals, which
        // we have not tested at 79 cards. Explicit removes the question.
        private static void WireNavigation(List<List<Selectable>> rows)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                for (int c = 0; c < row.Count; c++)
                {
                    var s = row[c];
                    if (s == null) continue;
                    var nav = new Navigation { mode = Navigation.Mode.Explicit };
                    // Left/right walk the list linearly, wrapping across rows, so
                    // holding a direction traverses every course in menu order.
                    nav.selectOnLeft = c > 0
                        ? row[c - 1]
                        : (r > 0 ? rows[r - 1][rows[r - 1].Count - 1] : null);
                    nav.selectOnRight = c < row.Count - 1
                        ? row[c + 1]
                        : (r < rows.Count - 1 ? rows[r + 1][0] : null);
                    // Up/down hold the column, clamping into short rows (the last
                    // row of a section, and any section with fewer than 5 courses).
                    nav.selectOnUp = r > 0
                        ? rows[r - 1][Mathf.Min(c, rows[r - 1].Count - 1)]
                        : null;
                    nav.selectOnDown = r < rows.Count - 1
                        ? rows[r + 1][Mathf.Min(c, rows[r + 1].Count - 1)]
                        : null;
                    s.navigation = nav;
                }
            }
        }

        private void LateUpdate()
        {
            if (!built || content == null || viewport == null) return;

            var es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || !sel.transform.IsChildOf(content)) return;

            // Walk up to the card root (the child whose parent is Content).
            Transform card = sel.transform;
            while (card != null && card.parent != content) card = card.parent;
            int i = cards.IndexOf(card as RectTransform);
            if (i < 0) return;

            float vh = viewport.rect.height;
            if (vh < 1f) return; // menu not laid out yet; try again next frame

            float cur = content.anchoredPosition.y;
            float target = cur;
            if (target > cardTop[i] - EdgeMargin) target = cardTop[i] - EdgeMargin;
            if (target < cardBottom[i] - vh + EdgeMargin) target = cardBottom[i] - vh + EdgeMargin;
            target = Mathf.Clamp(target, 0f, Mathf.Max(0f, contentHeight - vh));
            if (Mathf.Abs(target - cur) < 0.5f) return; // in view — don't fight a drag

            float ny = Mathf.Lerp(cur, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, ny);
        }
    }
}
