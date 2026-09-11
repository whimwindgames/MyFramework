using NUnit.Framework;
using UnityEngine;

public class FrameSafeAreaTests
{
    GameObject mObject;
    RectTransform mRoot, mTop, mDock, mBackground, mButton;
    FrameSafeArea mSafe;

    [SetUp]
    public void SetUp()
    {
        mObject = new GameObject("Viewport", typeof(RectTransform));
        mObject.SetActive(false);
        mRoot = (RectTransform)mObject.transform;
        mRoot.sizeDelta = new Vector2(1920, 1080);
        mTop = child("Top", mRoot);
        mDock = child("Dock", mRoot);
        mDock.anchorMax = new Vector2(1, 0);
        mDock.pivot = new Vector2(.5f, 0);
        mDock.sizeDelta = new Vector2(0, 230);
        mBackground = child("Background", mRoot);
        mButton = child("Button", mTop);
        mButton.anchorMin = mButton.anchorMax = Vector2.one;
        mButton.pivot = Vector2.one;
        mButton.sizeDelta = new Vector2(100, 90);
        mButton.anchoredPosition = new Vector2(-20, -25);
        mSafe = mObject.AddComponent<FrameSafeArea>();
        mSafe.Configure(new[] { mTop, mDock });
    }

    [TearDown]
    public void TearDown() => Object.DestroyImmediate(mObject);

    [TestCase(1920, 1080, 0, 0, 0, 0)]
    [TestCase(2436, 1125, 132, 63, 132, 0)]
    [TestCase(2400, 1080, 110, 0, 0, 0)]
    [TestCase(2400, 1080, 0, 0, 110, 0)]
    [TestCase(1920, 1080, 120, 45, 120, 30)]
    [TestCase(1080, 2400, 0, 80, 0, 150)]
    public void FitsPhysicalSafeBoundsWithLogicalCanvasAndFixedDock(int w, int h,
        int left, int bottom, int right, int top)
    {
        mRoot.sizeDelta = new Vector2(w * 1080f / h, 1080);
        mSafe.ApplySnapshot(snapshot(w, h, left, bottom, right, top));
        Rect expected = Rect.MinMaxRect(mRoot.rect.xMin + left * 1080f / h,
            mRoot.rect.yMin + bottom * 1080f / h, mRoot.rect.xMax - right * 1080f / h,
            mRoot.rect.yMax - top * 1080f / h);
        assertRect(bounds(mTop), expected);
        Rect dock = bounds(mDock);
        Assert.That(dock.xMin, Is.EqualTo(expected.xMin).Within(.001f));
        Assert.That(dock.xMax, Is.EqualTo(expected.xMax).Within(.001f));
        Assert.That(dock.yMin, Is.EqualTo(expected.yMin).Within(.001f));
        Assert.That(dock.height, Is.EqualTo(230).Within(.001f));
        assertRect(bounds(mBackground), mRoot.rect);
        Assert.That(mButton.parent, Is.SameAs(mTop));
        Assert.That(mButton.anchoredPosition, Is.EqualTo(new Vector2(-20, -25)));
        Assert.That(mButton.sizeDelta, Is.EqualTo(new Vector2(100, 90)));
    }

    [Test]
    public void NarrowSafeAreaKeepsAuthoredGapsUsingOptionalUniformScale()
    {
        mSafe.Configure(new[] { mTop, mDock }, new Vector2(1920, 0));
        mSafe.ApplySnapshot(snapshot(1920, 1080, 120, 45, 120, 30));
        assertRect(bounds(mTop), Rect.MinMaxRect(-840, -495, 840, 510));
        Assert.That(mTop.localScale.x, Is.EqualTo(.875f).Within(.0001f));
        Assert.That(bounds(mDock).height, Is.EqualTo(230 * .875f).Within(.001f));
        Rect button = bounds(mButton);
        Assert.That(button.xMax, Is.EqualTo(840 - 20 * .875f).Within(.001f));
        Assert.That(button.yMax, Is.EqualTo(510 - 25 * .875f).Within(.001f));
        Assert.That(button.width / button.height, Is.EqualTo(100f / 90f).Within(.0001f));
    }

    [Test]
    public void MinimumHeightScalesFixedContentWithoutChangingAspectRatio()
    {
        mSafe.Configure(new[] { mTop, mDock }, new Vector2(1920, 1080));
        mSafe.ApplySnapshot(snapshot(1920, 1080, 0, 108, 0, 0));
        Assert.That(mTop.localScale.x, Is.EqualTo(.9f).Within(.0001f));
        Assert.That(mTop.localScale.y, Is.EqualTo(.9f).Within(.0001f));
        assertRect(bounds(mTop), Rect.MinMaxRect(-960, -432, 960, 540));
    }

    [Test]
    public void RotationRepeatedApplicationAndFullScreenRestoreDoNotAccumulate()
    {
        FrameScreenSnapshot left = snapshot(2400, 1080, 110, 30, 0, 0);
        mRoot.sizeDelta = new Vector2(2400, 1080);
        mSafe.Configure(new[] { mTop, mDock }, new Vector2(1920, 0));
        for (int i = 0; i < 10; ++i)
        {
            mSafe.ApplySnapshot(left);
            mSafe.ApplySnapshot(left);
            mSafe.ApplySnapshot(snapshot(2400, 1080, 0, 30, 110, 0));
        }
        mSafe.ApplySnapshot(snapshot(2400, 1080, 0, 0, 0, 0));
        assertRect(bounds(mTop), mRoot.rect);
        Assert.That(mTop.anchorMin, Is.EqualTo(Vector2.zero));
        Assert.That(mTop.anchorMax, Is.EqualTo(Vector2.one));
        Assert.That(mTop.localScale, Is.EqualTo(Vector3.one));
        Assert.That(mDock.sizeDelta, Is.EqualTo(new Vector2(0, 230)));
    }

    [Test]
    public void RootResizeRecomputesEvenWhenPhysicalSnapshotIsUnchanged()
    {
        FrameScreenSnapshot screen = snapshot(1920, 1080, 100, 50, 100, 0);
        mSafe.ApplySnapshot(screen);
        mRoot.sizeDelta = new Vector2(960, 540);
        mSafe.ApplySnapshot(screen);
        assertRect(bounds(mTop), Rect.MinMaxRect(-430, -245, 430, 270));
    }

    [Test]
    public void InvalidTransitionSamplesPreserveLastValidLayout()
    {
        mSafe.ApplySnapshot(snapshot(1920, 1080, 100, 50, 0, 0));
        Rect before = bounds(mTop);
        mSafe.ApplySnapshot(default);
        mSafe.ApplySnapshot(new FrameScreenSnapshot(new Vector2Int(1920, 1080),
            new Rect(0, 0, 0, 0), ScreenOrientation.Unknown));
        mSafe.ApplySnapshot(new FrameScreenSnapshot(new Vector2Int(1920, 1080),
            new Rect(float.NaN, 0, 100, 100), ScreenOrientation.Unknown));
        assertRect(bounds(mTop), before);
    }

    [Test]
    public void ReconfigurationRestoresOldTargetsAndRetainsAuthoredPadding()
    {
        mTop.offsetMin = new Vector2(12, 18);
        mTop.offsetMax = new Vector2(-24, -30);
        mSafe.ApplySnapshot(snapshot(1920, 1080, 100, 50, 100, 40));
        assertRect(bounds(mTop), Rect.MinMaxRect(-848, -472, 836, 470));
        mSafe.Configure(new[] { mDock });
        Assert.That(mTop.offsetMin, Is.EqualTo(new Vector2(12, 18)));
        Assert.That(mTop.offsetMax, Is.EqualTo(new Vector2(-24, -30)));
    }

    [Test]
    public void NestedTargetsAreRejectedWithoutReparentingOrMutatingLayout()
    {
        Assert.Throws<System.ArgumentException>(() => mSafe.Configure(new[] { mButton }));
        Assert.That(mButton.parent, Is.SameAs(mTop));
        assertRect(bounds(mTop), mRoot.rect);
    }

    static RectTransform child(string name, RectTransform parent)
    {
        var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.sizeDelta = Vector2.zero;
        return rect;
    }

    static FrameScreenSnapshot snapshot(int w, int h, int l, int b, int r, int t) =>
        new(new Vector2Int(w, h), new Rect(l, b, w - l - r, h - b - t), ScreenOrientation.AutoRotation);

    Rect bounds(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector3 min = mRoot.InverseTransformPoint(corners[0]);
        Vector3 max = mRoot.InverseTransformPoint(corners[2]);
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    static void assertRect(Rect actual, Rect expected)
    {
        Assert.That(actual.xMin, Is.EqualTo(expected.xMin).Within(.002f));
        Assert.That(actual.yMin, Is.EqualTo(expected.yMin).Within(.002f));
        Assert.That(actual.xMax, Is.EqualTo(expected.xMax).Within(.002f));
        Assert.That(actual.yMax, Is.EqualTo(expected.yMax).Within(.002f));
    }
}
