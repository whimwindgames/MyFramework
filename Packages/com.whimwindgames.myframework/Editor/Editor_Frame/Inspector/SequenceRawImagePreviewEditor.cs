using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SequenceRawImagePreview))]
[CanEditMultipleObjects]
public class SequenceRawImagePreviewEditor : GameInspector
{
    private SerializedProperty mSlider;
    private SerializedProperty mLoop;
    private SerializedProperty mFPS;
    private SerializedProperty mPreviewInEditor;
    private void OnEnable()
    {
        mSlider = serializedObject.FindProperty("mSlider");
        mLoop = serializedObject.FindProperty("mLoop");
        mFPS = serializedObject.FindProperty("mFPS");
        mPreviewInEditor = serializedObject.FindProperty("mPreviewInEditor");
    }
    protected override void onGUI()
    {
        serializedObject.Update();

        var preview = target as SequenceRawImagePreview;
        space();
        label("RawImage序列帧预览");

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(mPreviewInEditor, new GUIContent("编辑器中预览"));
        EditorGUILayout.PropertyField(mLoop, new GUIContent("循环播放"));
        EditorGUILayout.PropertyField(mFPS, new GUIContent("播放帧率"));
        EditorGUILayout.PropertyField(mSlider, new GUIContent("预览进度"));
        if (EditorGUI.EndChangeCheck())
        {
            serializedObject.ApplyModifiedProperties();
            if (preview != null)
            {
                Undo.RecordObject(preview, "Refresh RawImage Sequence Preview");
                preview.EditorRefreshBySlider();
                EditorUtility.SetDirty(preview);
            }
            return;
        }

        space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (button("刷新帧列表"))
            {
                Undo.RecordObject(preview, "Refresh RawImage Sequence Frames");
                preview.EditorRefresh();
                EditorUtility.SetDirty(preview);
            }
            if (button("上一帧"))
            {
                Undo.RecordObject(preview, "Previous RawImage Sequence Frame");
                preview.EditorPreviousFrame();
                EditorUtility.SetDirty(preview);
            }
            if (button("下一帧"))
            {
                Undo.RecordObject(preview, "Next RawImage Sequence Frame");
                preview.EditorNextFrame();
                EditorUtility.SetDirty(preview);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (button("播放"))
            {
                Undo.RecordObject(preview, "Play RawImage Sequence Preview");
                preview.Play();
                EditorUtility.SetDirty(preview);
            }
            if (button("暂停"))
            {
                Undo.RecordObject(preview, "Pause RawImage Sequence Preview");
                preview.Pause();
                EditorUtility.SetDirty(preview);
            }
            if (button("继续"))
            {
                Undo.RecordObject(preview, "Resume RawImage Sequence Preview");
                preview.Resume();
                EditorUtility.SetDirty(preview);
            }
            if (button("停止"))
            {
                Undo.RecordObject(preview, "Stop RawImage Sequence Preview");
                preview.Stop();
                EditorUtility.SetDirty(preview);
            }
        }

        space();

        int frameCount = preview != null ? preview.EditorGetFrameCount() : 0;
        int curFrame = preview != null ? preview.EditorGetCurFrame() : 0;
        bool playing = preview != null && preview.EditorIsPlaying();

        label("当前状态");
        label("帧数量", frameCount.ToString());
        label("当前帧", frameCount > 0 ? curFrame + " / " + (frameCount - 1) : "无");
        label("播放状态", playing ? "播放中" : "未播放");

        serializedObject.ApplyModifiedProperties();

        if (preview != null && preview.EditorIsPlaying())
        {
            Repaint();
        }
    }
}