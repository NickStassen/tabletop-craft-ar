// Block models for the resource-pack atlas (BlockModelBaker.cs, BlockModels.cs). A state's model is baked on the
// main thread the first time Face() resolves it (TabletopRenderer prewarms every new palette entry before meshing);
// mesher workers then read it through IModelSource. Kept in a partial so ResourcePackAtlas.cs only has a one-line hook.
using System.Collections.Generic;
using System.Threading;

namespace TabletopCraft.World
{
    public sealed partial class ResourcePackAtlas : IModelSource, IModelAssets
    {
        BlockModels _blockModels;
        readonly List<sbyte> _alphaClass = new List<sbyte>();   // per layer: -1 unknown, 0 opaque, 1 cutout, 2 translucent
        readonly int _mainThread = Thread.CurrentThread.ManagedThreadId;   // the atlas is created by TabletopApp

        /// <summary>A mesher worker can meet a palette entry added by a block_update before the main thread prewarmed
        /// it. Texture loading and model baking are main-thread only, so Face() answers with an uncached flat colour;
        /// the changed section is dirty again and is re-meshed after the next prewarm.</summary>
        bool OffMainThread => Thread.CurrentThread.ManagedThreadId != _mainThread;
        static FaceKey Unresolved(string state) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(state) };

        /// <summary>Main thread only (called from Face for a state seen for the first time).</summary>
        /// <summary>[blocks] False when BlockEntityRenderer draws the block entities (set before the first Face call).</summary>
        public bool BlockEntityBoxes = true;

        void PrepareModel(string state, FaceKey[] faces)
        {
            if (_zip == null) return;
            if (_blockModels == null) _blockModels = new BlockModels(this) { EntityBoxes = BlockEntityBoxes };
            _blockModels.Prepare(state, faces);
        }

        /// <summary>Any thread: the baked model, or null if the state was not prepared or there is no pack. (Explicit,
        /// because the atlas already has a private Model(id) for model JSON.)</summary>
        BakedModel IModelSource.Model(string state) => _blockModels?.Model(state);

        Dictionary<string, object> IModelAssets.ReadJson(string path) => ReadJson(path);

        FaceKey IModelAssets.Texture(string texture, bool tinted, string state) => Key(texture, tinted, state);

        int IModelAssets.AlphaClass(int layer)
        {
            if (layer < 0 || layer >= _pixels.Count) return 0;
            while (_alphaClass.Count <= layer) _alphaClass.Add(-1);
            if (_alphaClass[layer] < 0)
            {
                sbyte c = 0;
                foreach (var p in _pixels[layer])
                {
                    if (p.a == 255) continue;
                    if (p.a != 0) { c = 2; break; }
                    c = 1;
                }
                _alphaClass[layer] = c;
            }
            return _alphaClass[layer];
        }
    }
}
