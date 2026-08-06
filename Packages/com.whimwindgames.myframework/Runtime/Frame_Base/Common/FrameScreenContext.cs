using System;
using UnityEngine;

// 与具体UI系统无关的屏幕快照，横屏、竖屏、刘海屏和超宽屏都使用同一份数据。
public readonly struct FrameScreenSnapshot : IEquatable<FrameScreenSnapshot>
{
	public readonly Vector2Int Size;
	public readonly Rect SafeArea;
	public readonly ScreenOrientation Orientation;

	public FrameScreenSnapshot(Vector2Int size, Rect safeArea, ScreenOrientation orientation)
	{
		Size = new(Mathf.Max(0, size.x), Mathf.Max(0, size.y));
		SafeArea = clampSafeArea(safeArea, Size);
		Orientation = orientation;
	}

	public bool isLandscape() { return Size.x >= Size.y; }
	public bool isPortrait() { return Size.y > Size.x; }
	public float getAspect() { return Size.y > 0 ? Size.x / (float)Size.y : 0.0f; }

	// x=left, y=bottom, z=right, w=top
	public Vector4 getSafeAreaInsets()
	{
		return new(SafeArea.xMin, SafeArea.yMin,
			Mathf.Max(0.0f, Size.x - SafeArea.xMax),
			Mathf.Max(0.0f, Size.y - SafeArea.yMax));
	}

	public Rect getNormalizedSafeArea()
	{
		if (Size.x <= 0 || Size.y <= 0)
		{
			return new(0.0f, 0.0f, 1.0f, 1.0f);
		}
		return new(SafeArea.x / Size.x, SafeArea.y / Size.y,
			SafeArea.width / Size.x, SafeArea.height / Size.y);
	}

	public bool Equals(FrameScreenSnapshot other)
	{
		return Size == other.Size && SafeArea == other.SafeArea && Orientation == other.Orientation;
	}
	public override bool Equals(object obj) { return obj is FrameScreenSnapshot other && Equals(other); }
	public override int GetHashCode() { return HashCode.Combine(Size, SafeArea, Orientation); }
	public static bool operator ==(FrameScreenSnapshot left, FrameScreenSnapshot right) { return left.Equals(right); }
	public static bool operator !=(FrameScreenSnapshot left, FrameScreenSnapshot right) { return !left.Equals(right); }

	private static Rect clampSafeArea(Rect value, Vector2Int size)
	{
		float xMin = Mathf.Clamp(value.xMin, 0.0f, size.x);
		float yMin = Mathf.Clamp(value.yMin, 0.0f, size.y);
		float xMax = Mathf.Clamp(value.xMax, xMin, size.x);
		float yMax = Mathf.Clamp(value.yMax, yMin, size.y);
		return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
	}
}

public static class FrameScreenContext
{
	private static FrameScreenSnapshot mCurrent = capture();
	public static event Action<FrameScreenSnapshot> changed;

	public static FrameScreenSnapshot getCurrent() { return mCurrent; }

	public static FrameScreenSnapshot capture()
	{
		return new(new(Screen.width, Screen.height), Screen.safeArea, Screen.orientation);
	}

	public static bool refresh(bool force = false)
	{
		FrameScreenSnapshot next = capture();
		if (!force && next == mCurrent)
		{
			return false;
		}
		mCurrent = next;
		changed?.Invoke(next);
		return true;
	}
}
