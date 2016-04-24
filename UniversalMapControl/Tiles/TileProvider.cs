using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Windows.Foundation;
using Windows.UI.Xaml.Media;

using UniversalMapControl.Interfaces;
using UniversalMapControl.Projections;

namespace UniversalMapControl.Tiles
{
	public class TileProvider : ITileProvider
	{
		protected const int TileWidth = 256;
		private readonly ILayerConfiguration _layerConfiguration;
		private Dictionary<int, Dictionary<string, ICanvasBitmapTile>> _tileCache = new Dictionary<int, Dictionary<string, ICanvasBitmapTile>>();

		public TileProvider(ILayerConfiguration layerConfiguration)
		{
			_layerConfiguration = layerConfiguration;
			ZoomLevelOffset = 0.25;
			LowerZoomLevelsToLoad = int.MaxValue;
		}


		public double ZoomLevelOffset { get; set; }

		/// <summary>
		/// Specifies how many lower zoom level should automatically be loaded.
		/// Use 0 to disable loading of lower layers, use int.MaxValue to load all lower levels.
		/// Default is int.MaxValue.
		/// </summary>
		public int LowerZoomLevelsToLoad { get; set; }


		/// <summary>
		/// This method calculates all required tiles for the current Map. The function calculates the smallest axis-aligned 
		/// bounding box possible for the current ViewPort and returns the Tiles required for the calculated bounding box. 
		/// This means that if the current Map has a Heading that is not a multiple of 90� 
		/// this function will return too many tiles.
		/// </summary>
		protected virtual Rect GetTileIndexBounds(Map map, Size windowSize, int zoomLevel)
		{
			double halfHeight = windowSize.Height / (TileWidth * 2);
			double halfWidth = windowSize.Width / (TileWidth * 2);

			Point centerTileIndex = GetTileIndex(map.ViewPortCenter, zoomLevel);
			Point topLeft = new Point(centerTileIndex.X - halfWidth, centerTileIndex.Y - halfHeight);
			Point bottomRight = new Point(centerTileIndex.X + halfWidth, centerTileIndex.Y + halfHeight);

			RotateTransform rotation = new RotateTransform { Angle = map.Heading, CenterY = centerTileIndex.Y, CenterX = centerTileIndex.X };

			Rect rect = new Rect(topLeft, bottomRight);
			Rect bounds = rotation.TransformBounds(rect);
			return bounds;
		}

		public virtual void RefreshTiles(Map parentMap)
		{
			int currentTileZoomLevel = (int)Math.Floor(parentMap.ZoomLevel + ZoomLevelOffset);

			Rect bounds = GetTileIndexBounds(parentMap, parentMap.RenderSize, currentTileZoomLevel);
			int startLevel = Math.Max(0, currentTileZoomLevel - LowerZoomLevelsToLoad);
			for (int z = startLevel; z <= currentTileZoomLevel; z++)
			{
				int factor = 1 << (currentTileZoomLevel - z);

				int left = (int)Math.Floor(bounds.Left / factor);
				int right = (int)Math.Ceiling(bounds.Right / factor);
				int top = (int)Math.Max(Math.Floor(bounds.Top / factor), 0);
				int maxY = (1 << z) - 1;
				int bottom = (int)Math.Min(Math.Ceiling(bounds.Bottom / factor), maxY);

				Dictionary<string, ICanvasBitmapTile> tiles;
				if (!_tileCache.TryGetValue(z, out tiles))
				{
					tiles = new Dictionary<string, ICanvasBitmapTile>();
					_tileCache.Add(z, tiles);
				}
				Dictionary<string, ICanvasBitmapTile> tilesToRemove = new Dictionary<string, ICanvasBitmapTile>(tiles);

				for (int x = left; x <= right; x++)
				{
					for (int y = top; y <= bottom; y++)
					{
						string key = string.Join("/", x, y, z);

						ICanvasBitmapTile existing;
						if (tiles.TryGetValue(key, out existing))
						{
							tilesToRemove.Remove(key);
						}
						if (existing == null || existing.IsDisposed)
						{
							Point position = GetViewPortPositionFromTileIndex(new Point(x, y), z);
							ILocation location = parentMap.ViewPortProjection.ToLocation(position, false);
							int indexX = SanitizeIndex(x, z);

							ICanvasBitmapTile tile = _layerConfiguration.CreateTile(indexX, y, z, location);

							_layerConfiguration.TileLoader.Enqueue(tile);

							tiles.Add(key, tile);
						}
					}
				}

				foreach (var oldTile in tilesToRemove)
				{
					tiles.Remove(oldTile.Key);
					oldTile.Value.Dispose();
				}
			}

			//Remove alle Tiles from not needed ZoomLevels
			foreach (KeyValuePair<int, Dictionary<string, ICanvasBitmapTile>> tilesPerZoom in _tileCache.Where(t => t.Key > currentTileZoomLevel).ToList())
			{
				_tileCache.Remove(tilesPerZoom.Key);
				foreach (ITile tile in tilesPerZoom.Value.Values)
				{
					tile.Dispose();
				}
			}
		}

		public virtual IEnumerable<ICanvasBitmapTile> GetTiles(double zoomLevel)
		{
			int tileZoomLevel = (int)Math.Floor(zoomLevel + ZoomLevelOffset);

			foreach (KeyValuePair<int, Dictionary<string, ICanvasBitmapTile>> tileLayer in _tileCache.OrderBy(t => t.Key))
			{
				if (tileLayer.Key > tileZoomLevel)
				{
					continue;
				}
				foreach (ICanvasBitmapTile tile in tileLayer.Value.Values)
				{
					yield return tile;
				}
			}
		}

		public void ResetTiles()
		{
			Dictionary<int, Dictionary<string, ICanvasBitmapTile>> tileCache = _tileCache;
			_tileCache = new Dictionary<int, Dictionary<string, ICanvasBitmapTile>>();

			foreach (Dictionary<string, ICanvasBitmapTile> layerCache in tileCache.Values)
			{
				foreach (ICanvasBitmapTile tile in layerCache.Values)
				{
					tile.Dispose();
				}
			}
		}

		protected virtual int SanitizeIndex(int index, int zoom)
		{
			int tileCount = 1 << zoom;

			index = index % tileCount;
			if (index < 0)
			{
				index += tileCount;
			}
			return index;
		}

		protected virtual Point GetViewPortPositionFromTileIndex(Point tileIndex, int zoom)
		{
			int z = 1 << zoom;
			double q = Wgs84WebMercatorProjection.MapWidth / z;

			double x = tileIndex.X * q - Wgs84WebMercatorProjection.HalfMapWidth;
			double y = tileIndex.Y * q - Wgs84WebMercatorProjection.HalfMapWidth;
			return new Point(x, y);
		}

		protected virtual Point GetTileIndex(Point location, int zoom, bool sanitize = true)
		{
			int z = 1 << zoom;
			double q = Wgs84WebMercatorProjection.MapWidth / z;

			int x = (int)Math.Floor(location.X / q) - z / 2;
			int y = (int)Math.Floor(location.Y / q) + z / 2;

			if (sanitize)
			{
				return new Point(SanitizeIndex(x, zoom), SanitizeIndex(y, zoom));
			}
			return new Point(x, y);
		}
	}
}