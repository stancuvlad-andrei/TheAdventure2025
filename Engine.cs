using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

    private int _levelWidth;
    private int _levelHeight;
    private DateTimeOffset _lastBombSpawnTime = DateTimeOffset.Now;
    private readonly double _bombSpawnInterval = 2000;
    private readonly int _minSpawnRadius = 10;
    private readonly int _maxSpawnRadius = 100;

    private readonly Random _random = new();

    private DateTimeOffset _lastPickupSpawnTime = DateTimeOffset.Now;
    private readonly double _pickupSpawnInterval = 10000;
    private readonly string _pickupTag = "PushPickup";


    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;
    }

    public void SetupWorld()
    {
        _player = new(_renderer);

        var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
        var level = JsonSerializer.Deserialize<Level>(levelContent);
        if (level == null)
        {
            throw new Exception("Failed to load level");
        }

        _levelWidth = level.Width!.Value * level.TileWidth!.Value;
        _levelHeight = level.Height!.Value * level.TileHeight!.Value;

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent);
            if (tileSet == null)
            {
                throw new Exception("Failed to load tile set");
            }

            foreach (var tile in tileSet.Tiles)
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tile.Id!.Value, tile);
            }

            _loadedTileSets.Add(tileSet.Name, tileSet);
        }

        if (level.Width == null || level.Height == null)
        {
            throw new Exception("Invalid level dimensions");
        }

        if (level.TileWidth == null || level.TileHeight == null)
        {
            throw new Exception("Invalid tile dimensions");
        }

        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value,
            level.Height.Value * level.TileHeight.Value));

        _currentLevel = level;
    }

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;

        _player?.UpdatePosition(up, down, left, right, (int)msSinceLastFrame);

        var pickupsToRemove = new List<int>();
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is TemporaryGameObject temp && temp.Tag == _pickupTag)
            {
                int dx = temp.Position.X - _player.X;
                int dy = temp.Position.Y - _player.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist < 32)
                {
                    _player.CanPushBomb = true;
                    pickupsToRemove.Add(temp.Id);
                }
            }
        }

        foreach (var id in pickupsToRemove)
        {
            _gameObjects.Remove(id);
        }

        if (_input.IsSpacePressed() && _player.CanPushBomb)
        {
            foreach (var gameObject in _gameObjects.Values)
            {
                if (gameObject is TemporaryGameObject bomb && bomb.Tag != _pickupTag)
                {
                    int dx = bomb.Position.X - _player.X;
                    int dy = bomb.Position.Y - _player.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    if (distance < 50)
                    {
                        double len = Math.Max(1, distance);
                        int pushX = (int)(dx / len * 32);
                        int pushY = (int)(dy / len * 32);

                        bomb.Position = (bomb.Position.X + pushX, bomb.Position.Y + pushY);

                        _player.CanPushBomb = false;
                        break;
                    }
                }
            }
        }

        if ((currentTime - _lastBombSpawnTime).TotalMilliseconds >= _bombSpawnInterval)
        {
            double angle = _random.NextDouble() * 2 * Math.PI;

            // Generate random distance within radius range
            int distance = _random.Next(_minSpawnRadius, _maxSpawnRadius);

            // Calculate offset from player's position
            int offsetX = (int)(Math.Cos(angle) * distance);
            int offsetY = (int)(Math.Sin(angle) * distance);

            // Calculate bomb position within world bounds
            int bombX = Math.Clamp(_player.X + offsetX, 0, _levelWidth);
            int bombY = Math.Clamp(_player.Y + offsetY, 0, _levelHeight);

            AddBomb(bombX, bombY);
            _lastBombSpawnTime = currentTime;
        }

        if ((currentTime - _lastPickupSpawnTime).TotalMilliseconds >= _pickupSpawnInterval)
        {
            int pickupX = _random.Next(0, _levelWidth);
            int pickupY = _random.Next(0, _levelHeight);
            AddPushPickup(pickupX, pickupY);
            _lastPickupSpawnTime = currentTime;
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        _renderer.CameraLookAt(_player!.X, _player!.Y);
        RenderTerrain();
        RenderAllObjects();

        if (_player.CanPushBomb)
        {
            _renderer.CameraLookAt(0, 0);

            int iconTex = _renderer.LoadTexture(Path.Combine("Assets", "hand.png"), out _);
            var src = new Rectangle<int>(0, 0, 27, 27);
            var dst = new Rectangle<int>(20, 20, 27, 27);

            _renderer.RenderTexture(iconTex, src, dst);
            _renderer.CameraLookAt(_player.X, _player.Y);
        }

        _renderer.PresentFrame();
    }


    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id);
        }

        _player?.Render(_renderer);
    }

    public void RenderTerrain()
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            for (int i = 0; i < _currentLevel.Width; ++i)
            {
                for (int j = 0; j < _currentLevel.Height; ++j)
                {
                    int? dataIndex = j * currentLayer.Width + i;
                    if (dataIndex == null)
                    {
                        continue;
                    }

                    var currentTileId = currentLayer.Data[dataIndex.Value] - 1;
                    if (currentTileId == null)
                    {
                        continue;
                    }

                    var currentTile = _tileIdMap[currentTileId.Value];

                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables()
    {
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is RenderableGameObject renderableGameObject)
            {
                yield return renderableGameObject;
            }
        }
    }

    private void AddBomb(int worldX, int worldY)
    {
        SpriteSheet spriteSheet = new(_renderer, Path.Combine("Assets", "BombExploding.png"), 1, 13, 32, 64, (16, 48));
        spriteSheet.Animations["Explode"] = new SpriteSheet.Animation
        {
            StartFrame = (0, 0),
            EndFrame = (0, 12),
            DurationMs = 2000,
            Loop = false
        };
        spriteSheet.ActivateAnimation("Explode");

        TemporaryGameObject bomb = new(spriteSheet, 2.1, (worldX, worldY));
        _gameObjects.Add(bomb.Id, bomb);
    }

    private void AddPushPickup(int worldX, int worldY)
    {
        SpriteSheet spriteSheet = new(_renderer, Path.Combine("Assets", "hand.png"), 1, 1, 27, 27, (13, 13)); // centru la mijloc

        spriteSheet.Animations["Idle"] = new SpriteSheet.Animation
        {
            StartFrame = (0, 0),
            EndFrame = (0, 0),
            DurationMs = 1000,
            Loop = true
        };
        spriteSheet.ActivateAnimation("Idle");

        TemporaryGameObject pickup = new(spriteSheet, 15.0, (worldX, worldY));
        pickup.Tag = _pickupTag;
        _gameObjects.Add(pickup.Id, pickup);
    }


}