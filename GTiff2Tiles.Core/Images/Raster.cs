﻿#pragma warning disable CA1031 // Do not catch general exception types

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GTiff2Tiles.Core.Constants.Image;
using GTiff2Tiles.Core.Geodesic;
using GTiff2Tiles.Core.Helpers;
using GTiff2Tiles.Core.Tiles;
using NetVips;

namespace GTiff2Tiles.Core.Images
{
    /// <summary>
    /// Class for creating raster tiles.
    /// </summary>
    public sealed class Raster : IImage
    {
        #region Properties

        #region Private

        /// <summary>
        /// This image's data.
        /// </summary>
        private NetVips.Image Data { get; }

        #endregion

        #region Public

        /// <inheritdoc />
        public Size Size { get; }

        /// <inheritdoc />
        public Coordinate MinCoordinate { get; }

        /// <inheritdoc />
        public Coordinate MaxCoordinate { get; }

        /// <inheritdoc />
        public bool IsDisposed { get; private set; }

        #endregion

        #endregion

        #region Constructor/Destructor

        /// <summary>
        /// Creates new <see cref="Raster"/> object.
        /// </summary>
        /// <param name="inputFileInfo">Input GeoTiff image.</param>
        public Raster(FileInfo inputFileInfo, long maxMemoryCache = 2147483648)
        {
            //Disable NetVips warnings for tiff.
            NetVipsHelper.DisableLog();

            #region Check parameters

            CheckHelper.CheckFile(inputFileInfo, true);

            #endregion

            bool memory = inputFileInfo.Length <= maxMemoryCache;
            Data = NetVips.Image.NewFromFile(inputFileInfo.FullName, memory, NetVips.Enums.Access.Random);

            //Get border coordinates и raster sizes.
            Size = new Size(Data.Width, Data.Height);
            (MinCoordinate, MaxCoordinate) = Gdal.Gdal.GetImageBorders(inputFileInfo, Size);
        }

        public Raster(byte[] inputBytes)
        {
            throw new NotImplementedException();

            //Disable NetVips warnings for tiff.
            NetVipsHelper.DisableLog();

            Data = NetVips.Image.NewFromBuffer(inputBytes, access: NetVips.Enums.Access.Random);

            //Get border coordinates и raster sizes.
            Size = new Size(Data.Width, Data.Height);
            //TODO: get coordinates without fileinfo
            //(MinCoordinate, MaxCoordinate) = Gdal.Gdal.GetImageBorders(inputFileInfo, Size);
        }

        public Raster(Stream inputStream)
        {
            throw new NotImplementedException();

            //Disable NetVips warnings for tiff.
            NetVipsHelper.DisableLog();

            Data = NetVips.Image.NewFromStream(inputStream, access: NetVips.Enums.Access.Random);

            //Get border coordinates и raster sizes.
            Size = new Size(Data.Width, Data.Height);
            //TODO: get coordinates without fileinfo
            //(MinCoordinate, MaxCoordinate) = Gdal.Gdal.GetImageBorders(inputFileInfo, Size);
        }

        /// <summary>
        /// Destructor.
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

        /// <summary>
        /// Actually disposes the data.
        /// </summary>
        /// <param name="disposing"></param>
        private void Dispose(bool disposing)
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

        #region Image modification

        /// <summary>
        /// Resizes tile before creating it
        /// </summary>
        /// <param name="tileImage">Basic image to resize</param>
        /// <param name="xScale"></param>
        /// <param name="yScale"></param>
        /// <param name="kernel"></param>
        /// <param name="interpolation"></param>
        /// <param name="isCentre"></param>
        /// <returns></returns>
        private static NetVips.Image Resize(NetVips.Image tileImage, double xScale, double yScale,
                                            string kernel = NetVips.Enums.Kernel.Lanczos3,
                                            string interpolation = Interpolations.Bicubic,
                                            bool isCentre = false)
        {
            // We could just use vips_resize if we use centre sampling convention
            if (isCentre) return tileImage.Resize(xScale, kernel, yScale);

            // Otherwise, we need to implement vips_resize for ourselves

            // Calculate integral box shrink
            // We will get the best quality (but be the slowest) if we let reduce
            // do all the work. Leave it the final 200 - 300% to do as a compromise
            // for efficiency.
            int xShirnk = Math.Max(1, (int)Math.Floor(1.0 / (xScale * 2.0)));
            int yShrink = Math.Max(1, (int)Math.Floor(1.0 / (yScale * 2.0)));

            // Fast, integral box-shrink
            if (yShrink > 1)
            {
                tileImage = tileImage.Shrinkv(yShrink);
                yScale *= yShrink;
            }

            if (xShirnk > 1)
            {
                tileImage = tileImage.Shrinkh(xShirnk);
                xScale *= xShirnk;
            }

            // Any residual downsizing
            if (yScale < 1.0) tileImage = tileImage.Reducev(1.0 / yScale, kernel, false);
            if (xScale < 1.0) tileImage = tileImage.Reduceh(1.0 / xScale, kernel, false);

            // Any upsizing
            if (!(xScale > 1.0) && !(yScale > 1.0)) return tileImage;
            // Floating point affine transformation
            //double id = isCentre ? 0.5 : 0.0;
            const double id = 0.0;

            // Floating point affine transformation
            using Interpolate interpolate = Interpolate.NewFromName(interpolation);
            if (xScale > 1.0 && yScale > 1.0)
                tileImage = tileImage.Affine(new[] { xScale, 0.0, 0.0, yScale }, interpolate, idx: id, idy: id,
                                             extend: NetVips.Enums.Extend.Copy);
            else if (xScale > 1.0)
                tileImage = tileImage.Affine(new[] { xScale, 0.0, 0.0, 1.0 }, interpolate, idx: id, idy: id,
                                             extend: NetVips.Enums.Extend.Copy);
            else
                tileImage = tileImage.Affine(new[] { 1.0, 0.0, 0.0, yScale }, interpolate, idx: id, idy: id,
                                             extend: NetVips.Enums.Extend.Copy);

            return tileImage;
        }

        private void AddBands(ref NetVips.Image image, int bands)
        {
            for (; image.Bands < bands;) image = image.Bandjoin(255);
        }

        private void AddBands(NetVips.Image[] images, int bands)
        {
            for (int index = 0; index < images.Length; index++) AddBands(ref images[index], bands);
        }

        #endregion

        #region GeoQuery

        /// <summary>
        /// Calculate size and positions to read/write.
        /// </summary>
        /// <param name="upperLeftX">Tile's upper left x coordinate.</param>
        /// <param name="upperLeftY">Tile's upper left y coordinate.</param>
        /// <param name="lowerRightX">Tile's lower right x coordinate.</param>
        /// <param name="lowerRightY">Tile's lower right y coordinate.</param>
        /// <returns><see cref="ValueTuple{T1, T2, T3, T4, T5, T6, T7, T8}"/> of x/y positions and sizes to read and write tiles.</returns>
        private (Area readArea, Area writeArea) GeoQuery(Coordinate minCoordinate, Coordinate maxCoordinate, Size tileSize)
        {
            //TODO probably move to Geodesic.Coordinate class?

            //Read from input geotiff in pixels.
            double readPosMinX = Size.Width * (minCoordinate.Longitude - MinCoordinate.Longitude) / (MaxCoordinate.Longitude - MinCoordinate.Longitude);
            double readPosMaxX = Size.Width * (maxCoordinate.Longitude - MinCoordinate.Longitude) / (MaxCoordinate.Longitude - MinCoordinate.Longitude);
            double readPosMinY = Size.Height - Size.Height * (maxCoordinate.Latitude - MinCoordinate.Latitude) / (MaxCoordinate.Latitude - MinCoordinate.Latitude);
            double readPosMaxY = Size.Height - Size.Height * (minCoordinate.Latitude - MinCoordinate.Latitude) / (MaxCoordinate.Latitude - MinCoordinate.Latitude);

            //If outside of tiff.
            readPosMinX = readPosMinX < 0.0 ? 0.0 : readPosMinX > Size.Width ? Size.Width : readPosMinX;
            readPosMaxX = readPosMaxX < 0.0 ? 0.0 : readPosMaxX > Size.Width ? Size.Width : readPosMaxX;
            readPosMinY = readPosMinY < 0.0 ? 0.0 : readPosMinY > Size.Height ? Size.Height : readPosMinY;
            readPosMaxY = readPosMaxY < 0.0 ? 0.0 : readPosMaxY > Size.Height ? Size.Height : readPosMaxY;

            //Output tile's borders in pixels.
            double tilePixMinX = readPosMinX.Equals(0.0) ? MinCoordinate.Longitude :
                                 readPosMinX.Equals(Size.Width) ? MaxCoordinate.Longitude : minCoordinate.Longitude;
            double tilePixMaxX = readPosMaxX.Equals(0.0) ? MinCoordinate.Longitude :
                                 readPosMaxX.Equals(Size.Width) ? MaxCoordinate.Longitude : maxCoordinate.Longitude;
            double tilePixMinY = readPosMaxY.Equals(0.0) ? MaxCoordinate.Latitude :
                                 readPosMaxY.Equals(Size.Height) ? MinCoordinate.Latitude : minCoordinate.Latitude;
            double tilePixMaxY = readPosMinY.Equals(0.0) ? MaxCoordinate.Latitude :
                                 readPosMinY.Equals(Size.Height) ? MinCoordinate.Latitude : maxCoordinate.Latitude;


            //Positions of dataset to write in tile.
            double writePosMinX = tileSize.Width - tileSize.Width * (maxCoordinate.Longitude - tilePixMinX) / (maxCoordinate.Longitude - minCoordinate.Longitude);
            double writePosMaxX = tileSize.Width - tileSize.Width * (maxCoordinate.Longitude - tilePixMaxX) / (maxCoordinate.Longitude - minCoordinate.Longitude);
            double writePosMinY = tileSize.Height * (maxCoordinate.Latitude - tilePixMaxY) / (maxCoordinate.Latitude - minCoordinate.Latitude);
            double writePosMaxY = tileSize.Height * (maxCoordinate.Latitude - tilePixMinY) / (maxCoordinate.Latitude - minCoordinate.Latitude);

            //Sizes to read and write.
            double readXSize = readPosMaxX - readPosMinX;
            double writeXSize = writePosMaxX - writePosMinX;
            double readYSize = Math.Abs(readPosMaxY - readPosMinY);
            double writeYSize = Math.Abs(writePosMaxY - writePosMinY);

            //Shifts.
            double readXShift = readPosMinX - (int)readPosMinX;
            readXSize += readXShift;
            double readYShift = readPosMinY - (int)readPosMinY;
            readYSize += readYShift;
            double writeXShift = writePosMinX - (int)writePosMinX;
            writeXSize += writeXShift;
            double writeYShift = writePosMinY - (int)writePosMinY;
            writeYSize += writeYShift;

            //If output image sides are lesser then 1 - make image 1x1 pixels to prevent division by 0.
            writeXSize = writeXSize > 1.0 ? writeXSize : 1.0;
            writeYSize = writeYSize > 1.0 ? writeYSize : 1.0;

            Area readArea = new Area((int)readPosMinX, (int)readPosMinY, (int)readXSize, (int)readYSize);
            Area writeArea = new Area((int)writePosMinX, (int)writePosMinY, (int)writeXSize, (int)writeYSize);

            return (readArea, writeArea);
        }

        private (Area readArea, Area writeArea) GeoQuery(ITile tile) => GeoQuery(tile.MinCoordinate, tile.MaxCoordinate, tile.Size);

        #endregion

        #region Create tile image

        /// <summary>
        /// Writes one tile of current zoom.
        /// <para/>Crops zoom directly from input image.
        /// </summary>
        private NetVips.Image CreateTileImage(NetVips.Image tileCache, ITile tile, int bands)
        {
            //Get postitions and sizes for current tile.
            (Area readArea, Area writeArea) = GeoQuery(tile);

            // Scaling calculations
            double xScale = (double)writeArea.Size.Width / readArea.Size.Width;
            double yScale = (double)writeArea.Size.Height / readArea.Size.Height;

            // Crop and resize tile
            NetVips.Image tempTileImage = Resize(tileCache.Crop(readArea.X, readArea.Y, readArea.Size.Width,
                                                                readArea.Size.Height), xScale, yScale);

            //TODO: extension method for NetVips.Image?
            // Add alpha channel if needed
            AddBands(ref tempTileImage, bands);

            // Make transparent image and insert tile
            return NetVips.Image.Black(tile.Size.Width, tile.Size.Height).NewFromImage(0, 0, 0, 0)
                          .Insert(tempTileImage, writeArea.X, writeArea.Y);
        }

        #endregion

        #region Create tile

        //private ITile CreateTile(int x, int y, int z, bool tmsCompatible, string tileExtension, int bands,
        //                         Size size)
        //{
        //    ITile tile = new Tiles.Tile(x, y, z, tmsCompatible: tmsCompatible, extension: tileExtension,
        //                                size: size);
        //    tile.D = WriteTileToEnumerable(tile, bands);

        //    return tile;
        //}

        //private async ValueTask<ITile> CreateTileAsync(int x, int y, int z, bool tmsCompatible, string tileExtension, int bands,
        //                                               Size size) =>
        //    await Task.Run(() => CreateTile(x, y, z, tmsCompatible, tileExtension, bands, size)).ConfigureAwait(false);

        #endregion

        #region WriteTile

        private void WriteTileToFile(NetVips.Image tileCache, ITile tile, int bands)
        {
            using NetVips.Image tileImage = CreateTileImage(tileCache, tile, bands);

            //TODO: Validate tileImage, not tile!
            //if (!tile.Validate(false)) return;

            tileImage.WriteToFile(tile.FileInfo.FullName);
        }

        private IEnumerable<byte> WriteTileToEnumerable(NetVips.Image tileCache, ITile tile, int bands)
        {
            using NetVips.Image tileImage = CreateTileImage(tileCache, tile, bands);
            //TODO: test this methods
            //return tileImage.WriteToBuffer(tile.Extension);
            return tileImage.WriteToMemory();
        }

        #endregion

        #region WriteTiles

        /// <inheritdoc />
        public async ValueTask WriteTilesToDirectoryAsync(DirectoryInfo outputDirectoryInfo, int minZ, int maxZ,
                                                          Size tileSize,
                                                          bool tmsCompatible = false,
                                                          string tileExtension = Constants.Extensions.Png,
                                                          int bands = Constants.Image.Raster.Bands,
                                                          IProgress<double> progress = null, int threadsCount = 0,
                                                          bool isPrintEstimatedTime = true, int tileCacheCount = 1000)
        {
            //TODO: profile argument (geodetic/mercator)

            #region Parameters checking

            progress ??= new Progress<double>();

            #endregion

            ParallelOptions parallelOptions = new ParallelOptions();
            if (threadsCount > 0) parallelOptions.MaxDegreeOfParallelism = threadsCount;

            Stopwatch stopwatch = isPrintEstimatedTime ? Stopwatch.StartNew() : null;
            int tilesCount = Tiles.Tile.GetCount(MinCoordinate, MaxCoordinate, minZ, maxZ, tmsCompatible, tileSize);
            double counter = 0.0;

            if (tilesCount <= 0) return;

            // Create tile cache to read data from it
            using NetVips.Image tileCache = Data.Tilecache(tileSize.Width, tileSize.Height, tileCacheCount, threaded: true);

            //For each zoom.
            for (int zoom = minZ; zoom <= maxZ; zoom++)
            {
                //Get tiles min/max numbers.
                (Number minNumber, Number maxNumber) = Tiles.Tile.GetNumbersFromCoords(MinCoordinate, MaxCoordinate,
                                                                                       zoom, tmsCompatible, tileSize);

                //For each tile on given zoom calculate positions/sizes and save as file.
                for (int tileY = minNumber.Y; tileY <= maxNumber.Y; tileY++)
                {
                    int y = tileY;
                    int z = zoom;

                    void MakeTile(int x)
                    {
                        //Create directories for the tile. The overall structure looks like: outputDirectory/zoom/x/y.png.
                        DirectoryInfo tileDirectoryInfo = new DirectoryInfo(Path.Combine(outputDirectoryInfo.FullName, $"{z}", $"{x}"));
                        CheckHelper.CheckDirectory(tileDirectoryInfo);

                        ITile tile = new Tiles.Tile(x, y, z, extension: tileExtension, tmsCompatible: tmsCompatible,
                                                    size: tileSize);

                        //Warning: OpenLayers requires replacement of tileY to tileY+1
                        tile.FileInfo = new FileInfo(Path.Combine(tileDirectoryInfo.FullName, $"{y}{tileExtension}"));

                        // ReSharper disable once AccessToDisposedClosure
                        WriteTileToFile(tileCache, tile, bands);

                        //Report progress.
                        counter++;
                        double percentage = counter / tilesCount * 100.0;
                        progress.Report(percentage);

                        //Estimated time left calculation.
                        ProgressHelper.PrintEstimatedTimeLeft(percentage, stopwatch);
                    }

                    await Task.Run(() => Parallel.For(minNumber.X, maxNumber.X + 1, parallelOptions, MakeTile))
                              .ConfigureAwait(false);
                }
            }
        }

        #endregion

        #endregion
    }
}