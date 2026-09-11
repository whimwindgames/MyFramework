using System;
using UnityEngine;

/// <summary>
/// Fits existing direct-child content containers to a full-screen UI root's safe area.
/// Backgrounds stay outside the target list. Uses FrameScreenContext in both AOT and hot UI.
/// Does not run in edit mode or change prefab hierarchy/authored child layout.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("MyFramework/UI/Safe Area")]
public sealed class FrameSafeArea : MonoBehaviour
{
    [SerializeField, Tooltip("Existing direct children to fit. Leave full-screen backgrounds out.")]
    private RectTransform[] mContentRoots = Array.Empty<RectTransform>();
    [SerializeField, Tooltip("Optional minimum logical content size. 0 disables scaling on that axis.")]
    private Vector2 mMinimumContentSize;

    private struct Layout
    {
        public RectTransform rect;
        public Vector2 anchorMin, anchorMax, position, size;
        public Vector3 scale;
    }

    private Layout[] mLayouts;
    private RectTransform mRoot;
    private FrameScreenSnapshot mLastSnapshot;
    private Vector2 mLastRootSize;
    private bool mApplied;
    private static int sLastSampleFrame = -1;

    public void Configure(RectTransform[] contentRoots, Vector2 minimumContentSize = default)
    {
        if (contentRoots == null) throw new ArgumentNullException(nameof(contentRoots));
        foreach (RectTransform target in contentRoots)
            if (target == null || target.parent != transform)
                throw new ArgumentException("Safe-area content must be a direct child of the full-screen root.",
                    nameof(contentRoots));
        if (!finite(minimumContentSize.x) || !finite(minimumContentSize.y) ||
            minimumContentSize.x < 0f || minimumContentSize.y < 0f)
            throw new ArgumentOutOfRangeException(nameof(minimumContentSize));

        restore();
        mLayouts = null;
        mContentRoots = (RectTransform[])contentRoots.Clone();
        mMinimumContentSize = minimumContentSize;
        if (Application.isPlaying && isActiveAndEnabled) Refresh();
    }

    private void OnEnable()
    {
        FrameScreenContext.changed += onScreenChanged;
        Refresh();
    }

    private void OnDisable()
    {
        FrameScreenContext.changed -= onScreenChanged;
        restore();
    }

    private void onScreenChanged(FrameScreenSnapshot snapshot)
    {
        // Root layout listeners can run before or after us. Apply after framework updates,
        // using the final logical viewport size instead of an intermediate root size.
        mApplied = false;
    }

    private void LateUpdate()
    {
        // AOT loading UI can appear before ScreenOrientationSystem is registered.
        // Share one sample per frame across all safe-area roots and the framework.
        if (sLastSampleFrame != Time.frameCount)
        {
            sLastSampleFrame = Time.frameCount;
            FrameScreenContext.refresh();
        }
        ApplySnapshot(FrameScreenContext.getCurrent());
    }

    public void Refresh()
    {
        FrameScreenContext.refresh();
        ApplySnapshot(FrameScreenContext.getCurrent());
    }

    /// <summary>
    /// Apply an explicit snapshot to a temporary preview/test instance. Invalid transition
    /// samples leave the last valid layout in place. Normal runtime uses Refresh/LateUpdate.
    /// </summary>
    public void ApplySnapshot(FrameScreenSnapshot snapshot)
    {
        mRoot ??= GetComponent<RectTransform>();
        Vector2 rootSize = mRoot.rect.size;
        Rect safe = snapshot.SafeArea;
        if (snapshot.Size.x <= 0 || snapshot.Size.y <= 0 ||
            rootSize.x <= 0f || rootSize.y <= 0f || safe.width <= 0f || safe.height <= 0f ||
            !finite(safe.x) || !finite(safe.y) || !finite(safe.width) || !finite(safe.height)) return;
        if (mApplied && snapshot == mLastSnapshot && rootSize == mLastRootSize) return;
        if (!captureLayout()) return;

        Rect normalized = snapshot.getNormalizedSafeArea();
        Vector2 available = Vector2.Scale(rootSize, normalized.size);
        float scale = 1f;
        if (mMinimumContentSize.x > 0f) scale = Mathf.Min(scale, available.x / mMinimumContentSize.x);
        if (mMinimumContentSize.y > 0f) scale = Mathf.Min(scale, available.y / mMinimumContentSize.y);
        foreach (Layout layout in mLayouts)
        {
            RectTransform rect = layout.rect;
            // Never move a target that the caller has since moved into another hierarchy.
            if (rect == null || rect.parent != mRoot) continue;
            rect.anchorMin = normalized.min + Vector2.Scale(normalized.size, layout.anchorMin);
            rect.anchorMax = normalized.min + Vector2.Scale(normalized.size, layout.anchorMax);
            // Expand stretched dimensions before uniform scaling so their final bounds still
            // span the safe area. Fixed-height docks retain their authored height times scale.
            rect.sizeDelta = layout.size + Vector2.Scale(available,
                layout.anchorMax - layout.anchorMin) * (1f / scale - 1f);
            rect.anchoredPosition = layout.position * scale;
            rect.localScale = new Vector3(layout.scale.x * scale, layout.scale.y * scale, layout.scale.z);
        }
        mLastSnapshot = snapshot;
        mLastRootSize = rootSize;
        mApplied = true;
    }

    private bool captureLayout()
    {
        if (mLayouts != null) return true;
        if (mContentRoots == null || mContentRoots.Length == 0) return false;
        // Validate before modifying any target. Content roots themselves must have unit XY
        // scale and no rotation; authored scales/rotations deeper in the hierarchy are retained.
        foreach (RectTransform rect in mContentRoots)
        {
            if (rect == null || rect.parent != mRoot ||
                !Mathf.Approximately(rect.localScale.x, 1f) ||
                !Mathf.Approximately(rect.localScale.y, 1f) ||
                Quaternion.Angle(rect.localRotation, Quaternion.identity) > .01f)
                return false;
        }
        mLayouts = new Layout[mContentRoots.Length];
        for (int i = 0; i < mContentRoots.Length; ++i)
        {
            RectTransform rect = mContentRoots[i];
            mLayouts[i] = new Layout { rect = rect, anchorMin = rect.anchorMin,
                anchorMax = rect.anchorMax, position = rect.anchoredPosition,
                size = rect.sizeDelta, scale = rect.localScale };
        }
        return true;
    }

    private void restore()
    {
        if (mLayouts != null)
            foreach (Layout layout in mLayouts)
            {
                if (layout.rect == null || layout.rect.parent != transform) continue;
                layout.rect.anchorMin = layout.anchorMin;
                layout.rect.anchorMax = layout.anchorMax;
                layout.rect.anchoredPosition = layout.position;
                layout.rect.sizeDelta = layout.size;
                layout.rect.localScale = layout.scale;
            }
        mApplied = false;
    }

    private static bool finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
