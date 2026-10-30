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
    // Makes the level-select card strip scroll when more cards exist than fit,
    // auto-scrolling to the selected card. Lives on the "Scroll View" GameObject.
    //
    // Vanilla layout keeps Content stretch-filled to the viewport with the
    // ContentSizeFitter horizontally Unconstrained, so Content never overflows and
    // the ScrollRect has nothing to scroll — the cards are simply centred and any
    // beyond the edges are clipped. When the cards don't fit we switch Content to a
    // preferred-width, left-anchored layout so it overflows and the ScrollRect can
    // move it; when they DO fit we touch nothing, leaving the menu pixel-identical
    // to vanilla.
    public class LevelSelectScroller : MonoBehaviour
    {
        private ScrollRect scrollRect;
        private RectTransform content;
        private RectTransform viewport;
        // Set in OnEnable when all cards fit: LateUpdate then does nothing and no
        // layout is changed, so the vanilla look is preserved exactly.
        private bool disabled;

        // Card sizeDelta springs between these (LevelSelectEntry.FixedUpdate).
        private const float IdleCardWidth = 640f;
        private const float SelectedCardWidth = 768f;
        // Keep-in-view inset from each viewport edge (px).
        private const float EdgeMargin = 32f;

        // Vanilla Content layout, captured the first time overflow layout is
        // applied so a later all-fits OnEnable can restore it instead of leaving
        // the mutated layout stuck under a disabled scroller.
        private bool overflowApplied;
        private ContentSizeFitter.FitMode origFit;
        private bool origHadFitter;
        private Vector2 origAnchorMin;
        private Vector2 origAnchorMax;
        private float origPivotX;
        private float origAnchoredX;

        public void Init(ScrollRect sr)
        {
            scrollRect = sr;
        }

        private void OnEnable()
        {
            // AddComponent fires OnEnable before Init runs, but we're on the
            // ScrollRect's own GameObject, so GetComponent always resolves it.
            if (scrollRect == null) scrollRect = GetComponent<ScrollRect>();
            if (scrollRect == null) { disabled = true; return; }

            content = scrollRect.content;
            viewport = scrollRect.viewport != null
                ? scrollRect.viewport
                : scrollRect.transform.GetChild(0) as RectTransform;
            if (content == null || viewport == null) { disabled = true; return; }

            // An unbuilt/zero rect would make the fit test meaningless (everything
            // "overflows" a 0-wide viewport) — fail safe into vanilla for this
            // enable; the next menu open re-evaluates against a real rect.
            if (viewport.rect.width < 1f) { disabled = true; return; }

            // Child 0 is the inactive card template; count only active cards.
            int n = 0;
            for (int i = 0; i < content.childCount; i++)
                if (content.GetChild(i).gameObject.activeSelf) n++;

            // Use the MAX (selected) width for the one card so the fit decision
            // never flickers as widths spring frame to frame.
            float needed = (n - 1) * IdleCardWidth + SelectedCardWidth;
            if (needed <= viewport.rect.width)
            {
                // Everything fits — vanilla layout. If a previous enable applied
                // overflow layout, put the original back rather than leaving the
                // mutated anchors/fitter stuck under a disabled scroller.
                if (overflowApplied) RestoreVanillaLayout();
                disabled = true;
                return;
            }
            disabled = false;

            // Overflow layout: preferred-width content, left-anchored, pinned to the
            // viewport's left edge. Idempotent — safe to re-apply every menu open.
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (!overflowApplied)
            {
                origHadFitter = fitter != null;
                origFit = origHadFitter ? fitter.horizontalFit : ContentSizeFitter.FitMode.Unconstrained;
                origAnchorMin = content.anchorMin;
                origAnchorMax = content.anchorMax;
                origPivotX = content.pivot.x;
                origAnchoredX = content.anchoredPosition.x;
                overflowApplied = true;
            }
            if (fitter != null)
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, content.pivot.y);
            content.anchoredPosition = new Vector2(0f, content.anchoredPosition.y);
        }

        private void RestoreVanillaLayout()
        {
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (origHadFitter && fitter != null) fitter.horizontalFit = origFit;
            content.anchorMin = origAnchorMin;
            content.anchorMax = origAnchorMax;
            content.pivot = new Vector2(origPivotX, content.pivot.y);
            content.anchoredPosition = new Vector2(origAnchoredX, content.anchoredPosition.y);
            overflowApplied = false;
        }

        private void LateUpdate()
        {
            if (disabled || content == null || viewport == null) return;

            var es = EventSystem.current;
            GameObject sel = es == null ? null : es.currentSelectedGameObject;
            if (sel == null || !sel.transform.IsChildOf(content)) return;

            // Walk up to the card root (the child whose parent is Content).
            Transform card = sel.transform;
            while (card != null && card.parent != content) card = card.parent;
            var cardRt = card as RectTransform;
            if (cardRt == null) return;

            // Card horizontal extent in viewport-local space. Content is a child of
            // the viewport, so shifting content.anchoredPosition.x by d shifts these
            // by d too — the delta to bring an edge in-bounds is a direct target.
            Vector3[] corners = new Vector3[4];
            cardRt.GetWorldCorners(corners);
            float left = float.MaxValue, right = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 lp = viewport.InverseTransformPoint(corners[i]);
                if (lp.x < left) left = lp.x;
                if (lp.x > right) right = lp.x;
            }

            Rect vr = viewport.rect;
            float vLeft = vr.xMin + EdgeMargin;
            float vRight = vr.xMax - EdgeMargin;

            float targetX = content.anchoredPosition.x;
            bool needMove = false;
            if (left < vLeft) { targetX += vLeft - left; needMove = true; }       // card past left edge → move right
            else if (right > vRight) { targetX += vRight - right; needMove = true; } // past right edge → move left
            if (!needMove) return; // card fully visible — don't fight a mouse drag

            // Never scroll past the ends. content.rect.width tracks the springing
            // card widths, so recompute every frame.
            float minX = Mathf.Min(0f, viewport.rect.width - content.rect.width);
            targetX = Mathf.Clamp(targetX, minX, 0f);

            float cur = content.anchoredPosition.x;
            float nx = Mathf.Lerp(cur, targetX, 1f - Mathf.Exp(-10f * Time.unscaledDeltaTime));
            content.anchoredPosition = new Vector2(nx, content.anchoredPosition.y);
        }
    }
}
