using System;
using UnityEngine;

namespace Cubes
{
    [Serializable]
    public struct BlockType
    {
        public Rect TexAtlasRect;

        /// <summary>The block is fully opaque with no way to see through.</summary>
        public static bool IsOpaque(int block) => block != Air;
        /// <summary>The block cannot be moved through.</summary>
        public static bool IsSolid(int block) => block != Air;

        public const int Air = 0;
        public const int Stone = 1;
    }
}
