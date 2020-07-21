﻿#pragma warning disable CA1031 // Do not catch general exception types

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GTiff2Tiles.Core.Constants;
using GTiff2Tiles.Core.Coordinates;
using GTiff2Tiles.Core.Enums;
using GTiff2Tiles.Core.Helpers;
using GTiff2Tiles.Core.Tiles;
using NetVips;

// ReSharper disable ClassWithVirtualMembersNeverInherited.Global
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global

namespace GTiff2Tiles.Core.Images
{
    /// <summary>
    /// Class for creating <see cref="RasterTile"/>s
    /// </summary>
    public class Raster : IImage
    {
        #region Properties

        #region Private

        /// <summary>
        /// This <see cref="Raster"/>'s data
        /// </summary>
        private Image Data { get; }

        #endregion

        #region Public

        /// <inheritdoc />
        public Size Size { get; }

        /// <inheritdoc />
        public GeoCoordinate MinCoordinate { get; }

        /// <inheritdoc />
        public GeoCoordinate MaxCoordinate { get; }

        /// <inheritdoc />
        public CoordinateType GeoCoordinateType { get; }

        /// <inheritdoc />
        public bool IsDisposed { get; private set; }

        #endregion

        #endregion

        #region Constructor/Destructor

        /// <summary>
        /// Creates new <see cref="Raster"/> object
        /// </summary>
        /// <param name="inputFileInfo">Input GeoTiff image</param>
        /// <param name="maxMemoryCache">Max size of input image to store in RAM
        /// <remarks><para/>2GB by default</remarks></param>
        /// <param name="coordinateType">Type of coordinates
        /// <remarks><para/><see cref="GeodeticCoordinate"/> by default</remarks></param>
        public Raster(FileInfo inputFileInfo, long maxMemoryCache = 2147483648,
                      CoordinateType coordinateType = CoordinateType.Geodetic)
        {
            // Disable NetVips warnings for tiff
            NetVipsHelper.DisableLog();

            #region Check parameters

            CheckHelper.CheckFile(inputFileInfo, true);

            #endregion

            bool memory = inputFileInfo.Length <= maxMemoryCache;
            Data = Image.NewFromFile(inputFileInfo.FullName, memory, NetVips.Enums.Access.Random);

            // Get border coordinates и raster sizes
            Size = new Size(Data.Width, Data.Height);

            GeoCoordinateType = coordinateType;
            (MinCoordinate, MaxCoordinate) = Gdal.Gdal.GetImageBorders(inputFileInfo, Size, GeoCoordinateType);
        }

        /// <summary>
        /// Creates new <see cref="Raster"/> object
        /// </summary>
        /// <param name="inputBytes"><see cref="IEnumerable{T}"/> of GeoTiff's
        /// <see cref="byte"/>s</param>
        /// <param name="coordinateType">Type of coordinates
        /// <remarks><para/><see cref="GeodeticCoordinate"/> by default</remarks></param>
        public Raster(IEnumerable<byte> inputBytes, CoordinateType coordinateType = CoordinateType.Geodetic)
        {
            // Disable NetVips warnings for tiff
            NetVipsHelper.DisableLog();

            Data = Image.NewFromBuffer(inputBytes.ToArray(), access: NetVips.Enums.Access.Random);

            // Get border coordinates и raster sizes
            Size = new Size(Data.Width, Data.Height);

            GeoCoordinateType = coordinateType;

            // TODO: get coordinates without fileinfo
            FileInfo inputFileInfo = new FileInfo("tmp.tif");
            Data.WriteToFile(inputFileInfo.FullName);
            (MinCoordinate, MaxCoordinate) = Gdal.Gdal.GetImageBorders(inputFileInfo, Size, GeoCoordinateType);
            inputFileInfo.Delete();
        }

        /// <summary>
        /// Creates new <see cref="Raster"/> object
        /// </summary>
        /// <param name="inputStream"><see cref="Stream"/> with GeoTiff</param>
        /// <param name="coordinateType">Type of coordinates
        /// <remarks><para/><see cref="GeodeticCoordinate"/> by default</remarks></param>
        public Raster(Stream inputStream, CoordinateType coordinateType = CoordinateType.Geodetic)
        {
            // Disable NetVips warnings for tiff
            NetVipsHelper.DisableLog();

            Data = Image.NewFromStream(inputStream, access: NetVips.Enums.Access.Random);

            // Get border coordinates и raster sizes
            Size = new Size(Data.Width, Data.Height);

            GeoCoordinateType = coordinateType;

            // TODO: get coordinates without fileinfo
            FileInfo inputFileInfo = new FileInfo("tmp.tif");
            Data.WriteToFile(inputFileInfo.FullName);
            (MinCoordinate, MaxCoordinate) = Gdal.Gdal.GetImageBorders(inputFileInfo, Size, GeoCoordinateType);
            inputFileInfo.Delete();
        }

        /// <summary>
        /// Destructor
        /// </summary>
        ~Raster() => Dispose(false);

        #endregion

        #region Methods

        #region Dispose

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc cref="Dispose()"/>
        /// <param name="disposing">Dispose static fields?</param>
        protected virtual void Dispose(bool disposing)
        {
            if (IsDisposed) return;

            if (disposing)
            {
                //Occurs only if called by programmer. Dispose static things here.
            }

            Data.Dispose();

            IsDisposed = true;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            try
            {
                Dispose();

                return default;
            }
            catch (Exception exception)
            {
                return new ValueTask(Task.FromException(exception));
            }
        }

        #endregion

        #region Create tile image

        /// <summary>
        /// Create <see cref="Image"/> for one <see cref="RasterTile"/>
        /// from input <see cref="Image"/> or tile cache
        /// </summary>
        /// <param name="tileCache">Source <see cref="Image"/>
        /// or tile cache</param>
        /// <param name="tile">Target <see cref="RasterTile"/></param>
        /// <param name="interpolation">Interpolation of ready tiles</param>
        /// <returns>Ready <see cref="Image"/> for <see cref="RasterTile"/></returns>
        public Image CreateTileImage(Image tileCache, RasterTile tile, string interpolation)
        {
            // Get postitions and sizes for current tile
            (Area readArea, Area writeArea) = Area.GetAreas(this, tile);

            // Scaling calculations
            double xScale = (double)writeArea.Size.Width / readArea.Size.Width;
            double yScale = (double)writeArea.Size.Height / readArea.Size.Height;

            // Crop and resize tile
            Image tempTileImage = tileCache.Crop((int)readArea.OriginCoordinate.X, (int)readArea.OriginCoordinate.Y,
                                                 readArea.Size.Width, readArea.Size.Height)
                                           .Resize(xScale, interpolation, yScale);

            // Add alpha channel if needed
            Band.AddDefaultBands(ref tempTileImage, tile.BandsCount);

            // Make transparent image and insert tile
            return Image.Black(tile.Size.Width, tile.Size.Height).NewFromImage(0, 0, 0, 0)
                        .Insert(tempTileImage, (int)writeArea.OriginCoordinate.X,
                                (int)writeArea.OriginCoordinate.Y);
        }

        #endregion

        #region WriteTile

        /// <summary>
        /// Gets data from source <see cref="Image"/>
        /// or tile cache for specified <see cref="RasterTile"/>
        /// and writes it to ready file
        /// </summary>
        /// <param name="tileCache">Source <see cref="Image"/>
        /// or tile cache</param>
        /// <param name="tile">Target <see cref="RasterTile"/></param>
        /// <param name="interpolation">Interpolation of ready tiles</param>
        public void WriteTileToFile(Image tileCache, RasterTile tile, string interpolation)
        {
            using Image tileImage = CreateTileImage(tileCache, tile, interpolation);

            // TODO: Validate tileImage, not tile!
            //if (!tile.Validate(false)) return;

            tileImage.WriteToFile(tile.FileInfo.FullName);
        }

        /// <summary>
        /// Gets data from source <see cref="Image"/>
        /// or tile cache for specified <see cref="RasterTile"/>
        /// and writes it to <see cref="IEnumerable{T}"/>
        /// </summary>
        /// <param name="tileCache">Source <see cref="Image"/>
        /// or tile cache</param>
        /// <param name="tile">Target <see cref="RasterTile"/></param>
        /// <param name="interpolation">Interpolation of ready tiles</param>
        /// <returns><see cref="RasterTile"/>'s <see cref="byte"/>s</returns>
        public IEnumerable<byte> WriteTileToEnumerable(Image tileCache, RasterTile tile,
                                                       string interpolation)
        {
            using Image tileImage = CreateTileImage(tileCache, tile, interpolation);

            // TODO: test this methods
            //return tileImage.WriteToBuffer(tile.Extension);
            return tileImage.WriteToMemory();
        }

        /// <summary>
        /// Writes specified <see cref="ITile"/> to <see cref="Channel"/>
        /// </summary>
        /// <param name="tile"><see cref="RasterTile"/> to write</param>
        /// <param name="channelWriter">Target <see cref="ChannelWriter{T}"/></param>
        /// <returns><see langword="true"/> if <see cref="ITile"/> was written;
        /// <see langword="false"/> otherwise</returns>
        public static bool WriteTileToChannel(ITile tile, ChannelWriter<ITile> channelWriter) =>
            tile.Validate(false) && channelWriter.TryWrite(tile);

        /// <summary>
        /// Gets data from source <see cref="Image"/>
        /// or tile cache for specified <see cref="RasterTile"/>
        /// and writes it to <see cref="ChannelWriter{T}"/>
        /// </summary>
        /// <param name="tileCache">Source <see cref="Image"/>
        /// or tile cache</param>
        /// <param name="tile">Target <see cref="RasterTile"/></param>
        /// <param name="channelWriter">Target <see cref="ChannelWriter{T}"/></param>
        /// <param name="interpolation">Interpolation of ready tiles</param>
        /// <returns><see langword="true"/> if <see cref="ITile"/> was written;
        /// <see langword="false"/> otherwise</returns>
        public bool WriteTileToChannel(Image tileCache, RasterTile tile, ChannelWriter<ITile> channelWriter,
                                       string interpolation)
        {
            tile.Bytes = WriteTileToEnumerable(tileCache, tile, interpolation);

            return WriteTileToChannel(tile, channelWriter);
        }

        #endregion

        #region WriteTiles

        /// <inheritdoc />
        public async ValueTask WriteTilesToDirectoryAsync(DirectoryInfo outputDirectoryInfo, int minZ, int maxZ,
                                                          bool tmsCompatible = false, Size tileSize = null,
                                                          string tileExtension = FileExtensions.Png,
                                                          string interpolation = Interpolations.Lanczos3,
                                                          int bandsCount = RasterTile.DefaultBandsCount,
                                                          int tileCacheCount = 1000, int threadsCount = 0,
                                                          IProgress<double> progress = null,
                                                          bool isPrintEstimatedTime = false)
        {
            #region Parameters checking

            progress ??= new Progress<double>();
            tileSize ??= Tile.DefaultSize;

            ParallelOptions parallelOptions = new ParallelOptions();
            if (threadsCount > 0) parallelOptions.MaxDegreeOfParallelism = threadsCount;

            #region Progress stuff

            Stopwatch stopwatch = isPrintEstimatedTime ? Stopwatch.StartNew() : null;
            int tilesCount = Number.GetCount(MinCoordinate, MaxCoordinate, minZ, maxZ, tmsCompatible, tileSize);
            double counter = 0.0;

            if (tilesCount <= 0) return;

            #endregion

            #endregion

            // Create tile cache to read data from it
            using Image tileCache = Data.Tilecache(tileSize.Width, tileSize.Height, tileCacheCount, threaded: true);

            // For each zoom
            for (int zoom = minZ; zoom <= maxZ; zoom++)
            {
                // Get tiles min/max numbers
                (Number minNumber, Number maxNumber) = GeoCoordinate.GetNumbers(MinCoordinate, MaxCoordinate,
                                                                                       zoom, tileSize.Width,
                                                                                tmsCompatible);

                // For each tile on given zoom calculate positions/sizes and save as file
                for (int tileY = minNumber.Y; tileY <= maxNumber.Y; tileY++)
                {
                    int y = tileY;
                    int z = zoom;

                    void MakeTile(int x)
                    {
                        // Create directories for the tile
                        // The overall structure looks like: outputDirectory/zoom/x/y.png
                        DirectoryInfo tileDirectoryInfo = new DirectoryInfo(Path.Combine(outputDirectoryInfo.FullName, $"{z}", $"{x}"));
                        CheckHelper.CheckDirectory(tileDirectoryInfo);

                        Number tileNumber = new Number(x, y, z);
                        RasterTile tile = new RasterTile(tileNumber, extension: tileExtension, tmsCompatible: tmsCompatible,
                                                          size: tileSize, bandsCount: bandsCount,
                                                          coordinateType: GeoCoordinateType)
                        {
                            // Warning: OpenLayers requires replacement of tileY to tileY+1
                            FileInfo = new FileInfo(Path.Combine(tileDirectoryInfo.FullName, $"{y}{tileExtension}"))
                        };

                        // ReSharper disable once AccessToDisposedClosure
                        WriteTileToFile(tileCache, tile, interpolation);

                        // Report progress
                        counter++;
                        double percentage = counter / tilesCount * 100.0;
                        progress.Report(percentage);

                        // Estimated time left calculation
                        ProgressHelper.PrintEstimatedTimeLeft(percentage, stopwatch);
                    }

                    await Task.Run(() => Parallel.For(minNumber.X, maxNumber.X + 1, parallelOptions, MakeTile))
                              .ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async ValueTask WriteTilesToChannelAsync(ChannelWriter<ITile> channelWriter, int minZ, int maxZ,
                                                        bool tmsCompatible = false, Size tileSize = null,
                                                        string interpolation = Interpolations.Lanczos3,
                                                        int bandsCount = RasterTile.DefaultBandsCount,
                                                        int tileCacheCount = 1000, int threadsCount = 0,
                                                        IProgress<double> progress = null,
                                                        bool isPrintEstimatedTime = false)
        {
            #region Parameters checking

            progress ??= new Progress<double>();
            tileSize ??= Tile.DefaultSize;

            #region Progress stuff

            Stopwatch stopwatch = isPrintEstimatedTime ? Stopwatch.StartNew() : null;
            int tilesCount = Number.GetCount(MinCoordinate, MaxCoordinate, minZ, maxZ, tmsCompatible, tileSize);
            double counter = 0.0;

            ParallelOptions parallelOptions = new ParallelOptions();
            if (threadsCount > 0) parallelOptions.MaxDegreeOfParallelism = threadsCount;

            if (tilesCount <= 0) return;

            #endregion

            #endregion

            // Create tile cache to read data from it
            using Image tileCache = Data.Tilecache(tileSize.Width, tileSize.Height, tileCacheCount, threaded: true);

            // For each zoom
            for (int zoom = minZ; zoom <= maxZ; zoom++)
            {
                // Get tiles min/max numbers
                (Number minNumber, Number maxNumber) = GeoCoordinate.GetNumbers(MinCoordinate, MaxCoordinate,
                                                                                zoom, tileSize.Width,
                                                                                tmsCompatible);

                // For each tile on given zoom calculate positions/sizes and save as file
                for (int tileY = minNumber.Y; tileY <= maxNumber.Y; tileY++)
                {
                    int y = tileY;
                    int z = zoom;

                    void MakeTile(int x)
                    {
                        Number tileNumber = new Number(x, y, z);
                        RasterTile tile = new RasterTile(tileNumber, tmsCompatible: tmsCompatible,
                                                    size: tileSize, bandsCount: bandsCount,
                                                    coordinateType: GeoCoordinateType);

                        // ReSharper disable once AccessToDisposedClosure
                        if (!WriteTileToChannel(tileCache, tile, channelWriter, interpolation)) return;

                        // Report progress
                        counter++;
                        double percentage = counter / tilesCount * 100.0;
                        progress.Report(percentage);

                        // Estimated time left calculation
                        ProgressHelper.PrintEstimatedTimeLeft(percentage, stopwatch);
                    }

                    await Task.Run(() => Parallel.For(minNumber.X, maxNumber.X + 1, parallelOptions, MakeTile))
                              .ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<ITile> WriteTilesToAsyncEnumerable(int minZ, int maxZ,
                                                                         bool tmsCompatible = false, Size tileSize = null,
                                                                         string interpolation = Interpolations.Lanczos3,
                                                                         int bandsCount = RasterTile.DefaultBandsCount,
                                                                         int tileCacheCount = 1000, int threadsCount = 0,
                                                                         IProgress<double> progress = null,
                                                                         bool isPrintEstimatedTime = false)
        {
            #region Parameters checking

            progress ??= new Progress<double>();
            tileSize ??= Tile.DefaultSize;
            threadsCount = threadsCount <= 0 ? Environment.ProcessorCount : threadsCount;

            #region Progress stuff

            Stopwatch stopwatch = isPrintEstimatedTime ? Stopwatch.StartNew() : null;
            int tilesCount = Number.GetCount(MinCoordinate, MaxCoordinate, minZ, maxZ, tmsCompatible, tileSize);
            double counter = 0.0;

            if (tilesCount <= 0) yield break;

            #endregion

            #endregion

            using SemaphoreSlim semaphoreSlim = new SemaphoreSlim(threadsCount);
            HashSet<Task<ITile>> tasks = new HashSet<Task<ITile>>();

            // Create tile cache to read data from it
            using Image tileCache = Data.Tilecache(tileSize.Width, tileSize.Height, tileCacheCount, threaded: true);

            // For each specified zoom
            for (int zoom = minZ; zoom <= maxZ; zoom++)
            {
                // Get tiles min/max numbers
                (Number minNumber, Number maxNumber) = GeoCoordinate.GetNumbers(MinCoordinate, MaxCoordinate,
                                                                                zoom, tileSize.Width,
                                                                                tmsCompatible);

                // For each tile on given zoom calculate positions/sizes and save as file
                for (int tileY = minNumber.Y; tileY <= maxNumber.Y; tileY++)
                {
                    // TODO: use Parallel.For somehow
                    for (int tileX = minNumber.X; tileX <= maxNumber.X; tileX++)
                    {
                        await semaphoreSlim.WaitAsync();

                        int x = tileX;
                        int y = tileY;
                        int z = zoom;

                        ITile MakeTile()
                        {
                            Number tileNumber = new Number(x, y, z);
                            RasterTile tile = new RasterTile(tileNumber, tmsCompatible: tmsCompatible,
                                                             size: tileSize, bandsCount: bandsCount,
                                                             coordinateType: GeoCoordinateType);

                            try
                            {
                                // ReSharper disable once AccessToDisposedClosure
                                tile.Bytes = WriteTileToEnumerable(tileCache, tile, interpolation);
                                //if (!tile.Validate(false)) return null;
                            }
                            finally
                            {
                                // Report progress
                                counter++;
                                double percentage = counter / tilesCount * 100.0;
                                progress.Report(percentage);

                                // Estimated time left calculation
                                ProgressHelper.PrintEstimatedTimeLeft(percentage, stopwatch);

                                // ReSharper disable once AccessToDisposedClosure
                                semaphoreSlim.Release();
                            }

                            return tile;
                        }

                        tasks.Add(Task.Run(MakeTile));
                    }

                    while (tasks.Count != 0)
                    {
                        Task<ITile> task = await Task.WhenAny(tasks).ConfigureAwait(false);
                        tasks.Remove(task);
                        ITile tile = await task.ConfigureAwait(false);

                        if (!Tile.Validate(tile, false)) continue;

                        yield return tile;
                    }

                    tasks.Clear();
                }
            }
        }

        #endregion

        #endregion
    }
}
