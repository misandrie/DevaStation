using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Robust.Shared.Random;

namespace Robust.Shared.Map
{
    [Virtual]
    internal class TileDefinitionManager : ITileDefinitionManager
    {
        protected readonly List<ITileDefinition> TileDefs;
        private readonly Dictionary<string, ITileDefinition> _tileNames;
        private readonly Dictionary<string, List<string>> _awaitingAliases;

        /// <summary>
        /// Default Constructor.
        /// </summary>
        public TileDefinitionManager()
        {
            TileDefs = new List<ITileDefinition>();
            _tileNames = new Dictionary<string, ITileDefinition>();
            _awaitingAliases = new();
        }

        public virtual void Initialize()
        {
        }

        public virtual void Register(ITileDefinition tileDef)
        {
            var name = tileDef.ID;
            // DevaStation start - hot-reload: Keep tiledefs in place
            if (_tileNames.TryGetValue(name, out var existing))
            {
                tileDef.AssignTileId(existing.TileId);
                TileDefs[existing.TileId] = tileDef;
                _tileNames[name] = tileDef;
                return;
            }
            // DevaStation end

            var id = checked((ushort) TileDefs.Count);
            tileDef.AssignTileId(id);
            TileDefs.Add(tileDef);
            _tileNames[name] = tileDef;
        }


        public Tile GetVariantTile(string name, IRobustRandom random)
        {
            var tileDef = this[name];
            return GetVariantTile(tileDef, random);
        }

        public Tile GetVariantTile(string name, System.Random random)
        {
            var tileDef = this[name];
            return GetVariantTile(tileDef, random);
        }

        public Tile GetVariantTile(ITileDefinition tileDef, IRobustRandom random)
        {
            return new Tile(tileDef.TileId, variant: random.NextByte(tileDef.Variants));
        }

        public Tile GetVariantTile(ITileDefinition tileDef, System.Random random)
        {
            return new Tile(tileDef.TileId, variant: random.NextByte(tileDef.Variants));
        }

        public ITileDefinition this[string name] => _tileNames[name];

        public ITileDefinition this[int id] => TileDefs[id];

        public bool TryGetDefinition(string name, [NotNullWhen(true)] out ITileDefinition? definition)
        {
            return _tileNames.TryGetValue(name, out definition);
        }

        public bool TryGetDefinition(int id, [NotNullWhen(true)] out ITileDefinition? definition)
        {
            if (id >= TileDefs.Count)
            {
                definition = null;
                return false;
            }

            definition = TileDefs[id];
            return true;
        }

        public int Count => TileDefs.Count;

        public IEnumerator<ITileDefinition> GetEnumerator()
        {
            return TileDefs.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
