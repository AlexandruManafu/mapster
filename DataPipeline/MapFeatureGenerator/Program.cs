﻿using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using CommandLine;
using Mapster.Common;
using Mapster.Common.MemoryMappedTypes;
using OSMDataParser;
using OSMDataParser.Elements;

namespace MapFeatureGenerator;

public static class Program
{
    private static MapData LoadOsmFile(ReadOnlySpan<char> osmFilePath)
    {
        var nodes = new ConcurrentDictionary<long, AbstractNode>();
        var ways = new ConcurrentBag<Way>();

        Parallel.ForEach(new PBFFile(osmFilePath), (blob, _) =>
        {
            switch (blob.Type)
            {
                case BlobType.Primitive:
                    var primitiveBlock = blob.ToPrimitiveBlock();
                    foreach (var primitiveGroup in primitiveBlock)
                        switch (primitiveGroup.ContainedType)
                        {
                            case PrimitiveGroup.ElementType.Node:
                                foreach (var node in primitiveGroup) nodes[node.Id] = (AbstractNode)node;
                                break;

                            case PrimitiveGroup.ElementType.Way:
                                foreach (var way in primitiveGroup) ways.Add((Way)way);
                                break;
                        }

                    break;
            }
        });

        var tiles = new Dictionary<int, List<long>>();
        foreach (var (id, node) in nodes)
        {
            var tileId = TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude));
            if (tiles.TryGetValue(tileId, out var nodeIds))
            {
                nodeIds.Add(id);
            }
            else
            {
                tiles[tileId] = new List<long>
                {
                    id
                };
            }
        }

        return new MapData
        {
            Nodes = nodes.ToImmutableDictionary(),
            Tiles = tiles.ToImmutableDictionary(),
            Ways = ways.ToImmutableArray()
        };
    }

    private static void CreateMapDataFile(ref MapData mapData, string filePath)
    {
        var usedNodes = new HashSet<long>();

        var featureIds = new List<long>();
        // var geometryTypes = new List<GeometryType>();
        // var coordinates = new List<(long id, (int offset, List<Coordinate> coordinates) values)>();

        var labels = new List<int>();
        // var propKeys = new List<(long id, (int offset, IEnumerable<string> keys) values)>();
        // var propValues = new List<(long id, (int offset, IEnumerable<string> values) values)>();

        using var fileWriter = new BinaryWriter(File.OpenWrite(filePath));
        var offsets = new Dictionary<int, long>(mapData.Tiles.Count);

        // Write FileHeader
        fileWriter.Write((long)1); // FileHeader: Version
        fileWriter.Write(mapData.Tiles.Count); // FileHeader: TileCount

        // Write TileHeaderEntry
        foreach (var tile in mapData.Tiles)
        {
            fileWriter.Write(tile.Key); // TileHeaderEntry: ID
            fileWriter.Write((long)0); // TileHeaderEntry: OffsetInBytes
        }

        foreach (var (tileId, _) in mapData.Tiles)
        {
            usedNodes.Clear();

            featureIds.Clear();
            labels.Clear();

            var totalCoordinateCount = 0;
            var totalPropertyCount = 0;

            var featuresData = new Dictionary<long, FeatureData>();

            foreach (var way in mapData.Ways)
            {
                var featureData = new FeatureData
                {
                    Id = way.Id,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>()),
                    PropertyKeys = (totalPropertyCount, new List<string>(way.Tags.Count)),
                    PropertyValues = (totalPropertyCount, new List<string>(way.Tags.Count))
                };

                featureIds.Add(way.Id);
                var geometryType = GeometryType.Polyline;

                labels.Add(-1);
                foreach (var tag in way.Tags)
                {
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featureData.PropertyKeys.keys.Count * 2 + 1;
                    }
                    featureData.PropertyKeys.keys.Add(tag.Key);
                    featureData.PropertyValues.values.Add(tag.Value);
                }

                foreach (var nodeId in way.NodeIds)
                {
                    var node = mapData.Nodes[nodeId];
                    usedNodes.Add(nodeId);

                    foreach (var (key, value) in node.Tags)
                    {
                        if (!featureData.PropertyKeys.keys.Contains(key))
                        {
                            featureData.PropertyKeys.keys.Add(key);
                            featureData.PropertyValues.values.Add(value);
                        }
                    }

                    featureData.Coordinates.coordinates.Add(new Coordinate(node.Latitude, node.Longitude));
                }

                if (featureData.Coordinates.coordinates[0] == featureData.Coordinates.coordinates[^1])
                {
                    geometryType = GeometryType.Polygon;
                }
                featureData.GeometryType = (byte)geometryType;

                totalPropertyCount += featureData.PropertyKeys.keys.Count;
                totalCoordinateCount += featureData.Coordinates.coordinates.Count;

                if (featureData.PropertyKeys.keys.Count != featureData.PropertyValues.values.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featuresData.Add(way.Id, featureData);
            }

            foreach (var (nodeId, node) in mapData.Nodes.Where(n => !usedNodes.Contains(n.Key)))
            {
                featureIds.Add(nodeId);

                var FeatureTypeKeys = new List<string>();
                var FeatureTypeValues = new List<string>();

                labels.Add(-1);
                for (var i = 0; i < node.Tags.Count; ++i)
                {
                    var tag = node.Tags[i];
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + FeatureTypeKeys.Count * 2 + 1;
                    }

                    FeatureTypeKeys.Add(tag.Key);
                    FeatureTypeValues.Add(tag.Value);
                }

                if (FeatureTypeKeys.Count != FeatureTypeValues.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featuresData.Add(nodeId, new FeatureData
                {
                    Id = nodeId,
                    GeometryType = (byte)GeometryType.Point,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>
                    {
                        new Coordinate(node.Latitude, node.Longitude)
                    }),
                    PropertyKeys = (totalPropertyCount, FeatureTypeKeys),
                    PropertyValues = (totalPropertyCount, FeatureTypeValues)
                });

                totalPropertyCount += FeatureTypeKeys.Count;
                ++totalCoordinateCount;
            }

            offsets.Add(tileId, fileWriter.BaseStream.Position);

            // Write TileBlockHeader
            fileWriter.Write(featureIds.Count); // TileBlockHeader: FeatureCount
            fileWriter.Write(totalCoordinateCount); // TileBlockHeader: CoordinateCount
            fileWriter.Write(totalPropertyCount * 2); // TileBlockHeader: StringCount
            fileWriter.Write(0); //TileBlockHeader: CharactersCount

            var coPosition = fileWriter.BaseStream.Position;
            fileWriter.Write((long)0); // TileBlockHeader: CoordinatesOffsetInBytes (placeholder)

            var soPosition = fileWriter.BaseStream.Position;
            fileWriter.Write((long)0); // TileBlockHeader: StringsOffsetInBytes (placeholder)

            var choPosition = fileWriter.BaseStream.Position;
            fileWriter.Write((long)0); // TileBlockHeader: CharactersOffsetInBytes (placeholder)

            // Write MapFeatures
            for (var i = 0; i < featureIds.Count; ++i)
            {
                var featureData = featuresData[featureIds[i]];

                fileWriter.Write(featureIds[i]); // MapFeature: Id
                fileWriter.Write(labels[i]); // MapFeature: LabelOffset
                fileWriter.Write(featureData.GeometryType); // MapFeature: GeometryType
                fileWriter.Write(featureData.Coordinates.offset); // MapFeature: CoordinateOffset
                fileWriter.Write(featureData.Coordinates.coordinates.Count); // MapFeature: CoordinateCount
                fileWriter.Write(featureData.PropertyKeys.offset * 2); // MapFeature: PropertiesOffset 
                fileWriter.Write(featureData.PropertyKeys.keys.Count); // MapFeature: PropertyCount
            }

            var currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)coPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CoordinatesOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];

                foreach (var c in featureData.Coordinates.coordinates)
                {
                    fileWriter.Write(c.Latitude); // Coordinate: Latitude
                    fileWriter.Write(c.Longitude); // Coordinate: Longitude
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)soPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: StringsOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);

            var typeDict = new Dictionary<string, FeatureType>
            {
                { "natural", FeatureType.Natural },
                { "place", FeatureType.Place },
                { "water", FeatureType.Water },
                { "railway", FeatureType.Railway },
                { "highway", FeatureType.Highway },
                { "landuse", FeatureType.Landuse },
                { "building", FeatureType.Building },
                { "leisure", FeatureType.Leisure },
                { "amenity", FeatureType.Amenity },
                { "boundary", FeatureType.Boundary },
                { "admin_level", FeatureType.AdminLevel },
            
            };
            var subTypeDict = new Dictionary<string, FeatureSubType>
            {
                { "square", FeatureSubType.Square },
                { "construction", FeatureSubType.Construction },
                { "fell", FeatureSubType.Fell },
                { "reservoir", FeatureSubType.Reservoir },
                { "meadow", FeatureSubType.Meadow },
                { "secondary", FeatureSubType.Secondary },
                { "city", FeatureSubType.City },
                { "rock", FeatureSubType.Rock },
                { "water", FeatureSubType.Water },
                { "scree", FeatureSubType.Scree },
                { "residential", FeatureSubType.Residential },
                { "basin", FeatureSubType.Basin },
                { "grassland", FeatureSubType.Grassland },
                { "locality", FeatureSubType.Locality },
                { "cemetery", FeatureSubType.Cemetery },
                { "farm", FeatureSubType.Farm },
                { "industrial", FeatureSubType.Industrial },
                { "primary", FeatureSubType.Primary },
                { "tree_row", FeatureSubType.TreeRow },
                { "forest", FeatureSubType.Forest },
                { "wood", FeatureSubType.Wood },
                { "moor", FeatureSubType.Moor },
                { "greenfield", FeatureSubType.Greenfield },
                { "unclassified", FeatureSubType.Unclassified },
                { "bare_rock", FeatureSubType.BareRock },
                { "trunk", FeatureSubType.Trunk },
                { "brownfield", FeatureSubType.Brownfield },
                { "road", FeatureSubType.Road },
                { "orchard", FeatureSubType.Orchard },
                { "scrub", FeatureSubType.Scrub },
                { "allotments", FeatureSubType.Allotments },
                { "administrative", FeatureSubType.Administrative },
                { "recreation_ground", FeatureSubType.RecreationGround },
                { "natural", FeatureSubType.Natural },
                { "heath", FeatureSubType.Heath },
                { "winter_sports", FeatureSubType.WinterSports },
                { "military", FeatureSubType.Military },
                { "quarry", FeatureSubType.Quarry },
                { "grass", FeatureSubType.Grass },
                { "2", FeatureSubType.Two },
                { "tertiary", FeatureSubType.Tertiary },
                { "commercial", FeatureSubType.Commercial },
                { "town", FeatureSubType.Town },
                { "hamlet", FeatureSubType.Hamlet },
                { "wetland", FeatureSubType.Wetland },
                { "sand", FeatureSubType.Sand },
		        { "motorway", FeatureSubType.Motorway },
                { "beach", FeatureSubType.Beach }
            };

            var stringOffset = 0;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    // Add 1 to the offset for keys
                    string v = featureData.PropertyValues.values[i];

                    fileWriter.Write(stringOffset);
                    fileWriter.Write(1);
                    stringOffset += 1;

                    // If sub-property exists in enum, add 1 to offset
                    // otherwise, add the whole string
                    if (SubPropToEnumCode(subTypeDict, v) == FeatureSubType.Unknown)
                    {
                        fileWriter.Write(stringOffset);
                        fileWriter.Write(v.Length);
                        stringOffset += v.Length;
                    }
                    else
                    {
                        fileWriter.Write(stringOffset);
                        fileWriter.Write(1);
                        stringOffset += 1;
                    }
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.Seek((int)choPosition, SeekOrigin.Begin);
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CharactersOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.Seek((int)currentPosition, SeekOrigin.Begin);
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                for (var i = 0; i < featureData.PropertyKeys.keys.Count; ++i)
                {
                    string k = featureData.PropertyKeys.keys[i];
                    fileWriter.Write((short)PropToEnumCode(typeDict, k));
                    
                    string v = featureData.PropertyValues.values[i];
                    if (SubPropToEnumCode(subTypeDict, v) != FeatureSubType.Unknown)
                    {
                        fileWriter.Write((short)SubPropToEnumCode(subTypeDict, v));
                    }
                    else
                    {
                        foreach (var c in v)
                        {
                            fileWriter.Write((short)c);
                        }
                    }
                }
            }
        }

        fileWriter.Seek(Marshal.SizeOf<FileHeader>(), SeekOrigin.Begin);
        foreach (var (tileId, offset) in offsets)
        {
            fileWriter.Write(tileId);
            fileWriter.Write(offset);
        }

        fileWriter.Flush();
    }
    
    private static FeatureType PropToEnumCode(Dictionary<string, FeatureType> dict, string propName)
    {
        if (dict.ContainsKey(propName))
            return dict[propName];
        return FeatureType.Unknown;
    }
    
    private static FeatureSubType SubPropToEnumCode(Dictionary<string, FeatureSubType> dict,string subPropName)
    {
        if (dict.ContainsKey(subPropName))
            return dict[subPropName];

        return FeatureSubType.Unknown;
    }

    public static void Main(string[] args)
    {
        Options? arguments = null;
        var argParseResult =
            Parser.Default.ParseArguments<Options>(args).WithParsed(options => { arguments = options; });

        if (argParseResult.Errors.Any())
        {
            Environment.Exit(-1);
        }

        var mapData = LoadOsmFile(arguments!.OsmPbfFilePath);
        CreateMapDataFile(ref mapData, arguments!.OutputFilePath!);
    }

    public class Options
    {
        [Option('i', "input", Required = true, HelpText = "Input osm.pbf file")]
        public string? OsmPbfFilePath { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output binary file")]
        public string? OutputFilePath { get; set; }
    }

    private readonly struct MapData
    {
        public ImmutableDictionary<long, AbstractNode> Nodes { get; init; }
        public ImmutableDictionary<int, List<long>> Tiles { get; init; }
        public ImmutableArray<Way> Ways { get; init; }
    }

    private struct FeatureData
    {
        public long Id { get; init; }

        public byte GeometryType { get; set; }
        public (int offset, List<Coordinate> coordinates) Coordinates { get; init; }
        public (int offset, List<string> keys) PropertyKeys { get; init; }
        public (int offset, List<string> values) PropertyValues { get; init; }
    }
}