using System;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using static System.FormattableString;

namespace Cubes
{
    public class DebugOverlay : MonoBehaviour
    {
        [SerializeField]
        private ChunkLoader _chunkLoader;

        [SerializeField]
        private bool _show = true;

        private string _fps;
        private string _mainThread;
        private string _renderThread;

        private ProfilerRecorder _mainThreadTimeRecorder;
        private ProfilerRecorder _renderThreadTimeRecorder;
        private ProfilerRecorder _renderTrianglesRecorder;
        private ProfilerRecorder _systemMemoryRecorder;

        private void OnEnable()
        {
            // _mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
            _mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Total Frame Time");
            _renderThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "CPU Render Thread Frame Time");
            _renderTrianglesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _systemMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
        }

        private void OnDisable()
        {
            _mainThreadTimeRecorder.Dispose();
            _renderThreadTimeRecorder.Dispose();
            _renderTrianglesRecorder.Dispose();
            _systemMemoryRecorder.Dispose();
        }

        private void Start()
        {
            UpdateStats();
        }

        private async void UpdateStats()
        {
            while (!destroyCancellationToken.IsCancellationRequested)
            {
                _fps = Invariant($"{(int)(1 / Time.deltaTime)} fps ({Time.deltaTime * 1000:F1} ms)");
                _mainThread = Invariant($"Main Thread: {_mainThreadTimeRecorder.LastValue * 1e-6f:F1} ms");
                _renderThread = Invariant($"Render Thread: {_renderThreadTimeRecorder.LastValue * 1e-6f:F1} ms");

                await Awaitable.WaitForSecondsAsync(0.2f);
            }
        }

        private void OnGUI()
        {
            if (Event.current.keyCode == KeyCode.F3 && Event.current.type == EventType.KeyDown)
                _show = !_show;

            if (!_show)
                return;

            var camPos = Camera.main.transform.position;
            var blockPos = (int3)math.floor(camPos);
            var chunkPos = (int3)math.floor(camPos / Chunk.Size);

            static void Label(string text)
            {
                GUILayout.Label(text, Styles.Label, GUILayout.ExpandWidth(false));
            }

            var scale = Screen.height / 800f;

            var safeArea = Screen.safeArea;
            safeArea.y = Screen.height - safeArea.yMax;
            safeArea.min /= scale;
            safeArea.max /= scale;

            GUILayout.BeginArea(safeArea);
            GUI.matrix = Matrix4x4.Scale(Vector3.one * scale);

            GUI.backgroundColor = new Color(0, 0, 0, 0.2f);
            Label(_fps);

            Label(Invariant($"Block: {blockPos.x} {blockPos.y} {blockPos.z}"));
            Label(Invariant($"Chunk: {chunkPos.x} {chunkPos.y} {chunkPos.z}"));
            GUILayout.Space(10);
            Label(Invariant($"Chunks: {_chunkLoader.LoadedChunkCount}/{_chunkLoader.TrackedChunkCount}"));
            Label(Invariant($"Blocks: {_chunkLoader.BlocksInMemoryCount} ({BytesToMB(_chunkLoader.BlocksInMemoryCount)})"));
            Label(Invariant($"Meshes: {_chunkLoader.MeshCount} ({BytesToMB(_chunkLoader.MeshMemoryUsedBytes)})"));
            GUILayout.Space(10);
            Label(_mainThread);
            Label(_renderThread);
            Label(Invariant($"Tris: {_renderTrianglesRecorder.LastValue / 1000f:F1}k"));
            Label(Invariant($"System Memory: {BytesToMB(_systemMemoryRecorder.LastValue)}"));

            GUILayout.FlexibleSpace();

            Label(Invariant($"Last Loaded: {_chunkLoader.LastChunksLoadedCount}"));
            Label(Invariant($"Last Rendered: {_chunkLoader.LastChunksRenderedCount}"));
            Label(Invariant($"Last Update: {_chunkLoader.LastChunkUpdateDurationMs:F2} ms"));
            GUILayout.Space(10);

            GUI.backgroundColor = Color.white;
            Buttons();

            GUILayout.Space(10);

            GUI.matrix = Matrix4x4.identity;
            GUILayout.EndArea();
        }

        private void Buttons()
        {
            if (GUILayout.Button("Unload", GUILayout.ExpandWidth(false)))
            {
                _chunkLoader.Unload();
            }
        }

        private static FormattableString BytesToMB(long bytes)
        {
            return $"{bytes / (1000 * 1000)} MB";
        }

        private static class Styles
        {
            public static readonly GUIStyle Label = new("Label")
            {
                margin = new(),
                padding = new(2, 2, 2, 2),
                wordWrap = false,
                clipping = TextClipping.Overflow,
                normal = new()
                {
                    textColor = Color.white,
                    background = Texture2D.whiteTexture
                }
            };
        }
    }
}
