using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Cubes
{
    /// <summary>
    /// Draw debug shapes from anywhere in code like <see cref="Debug.DrawLine"/>
    /// </summary>
    public static class DebugGizmos
    {
        public static void DrawWireCube(float3 center, float3 size, Color color, float time)
        {
            GetInstance()._cubes.Add(new()
            {
                Center = center,
                Size = size,
                Color = color,
                Timer = time
            });
        }

        private struct Cube
        {
            public float3 Center, Size;
            public Color Color;
            public float Timer;
        }

        private static Behaviour _instance;

        private static Behaviour GetInstance()
        {
            if (_instance == null)
            {
                var go = new GameObject(nameof(DebugGizmos));
                Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<Behaviour>();
            }
            return _instance;
        }

        private class Behaviour : MonoBehaviour
        {
            public List<Cube> _cubes = new();

            private void OnDrawGizmos()
            {
                for (int i = 0; i < _cubes.Count; i++)
                {
                    var c = _cubes[i];

                    Gizmos.color = c.Color;
                    Gizmos.DrawWireCube(c.Center, c.Size);

                    c.Timer -= Time.deltaTime;
                    if (c.Timer <= 0)
                        _cubes.RemoveAtSwapBack(i--);
                    else
                        _cubes[i] = c;
                }
            }
        }
    }
}
