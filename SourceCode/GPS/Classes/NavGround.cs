using AgOpenGPS.Core.DrawLib;
using AgOpenGPS.Core.Models;
using GMap.NET;
using GMap.NET.MapProviders;
using OpenTK.Graphics.OpenGL;
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace AgOpenGPS
{
    // The navigation map: while no field is open the ground of the 3D view is a map (OpenStreetMap or satellite) of
    // the area around the tractor instead of the grid. It has two layers: a wide one (zoom 16, about 3.3 km across)
    // so there is map far ahead, and a sharp one on top near the tractor (zoom 18, about 0.4 m a pixel, about 820 m
    // across). Each is a square of map tiles put together on a worker thread (GMap.NET downloads them, or reads them
    // from its disk cache when there is no internet) and drawn as one texture; a square is made again when the tractor
    // nears its edge. Missing tiles stay a plain grey.
    public sealed class NavGround : IDisposable
    {
        private readonly Control owner;      // the UI thread: textures are made there, where the OpenGL context is
        private GMapProvider provider;
        private string source;
        private int generation;              // a newer request (other source, cleared) makes an older result useless

        private readonly Layer wide = new Layer(NavMapRules.Zoom, -0.05);
        private readonly Layer near = new Layer(NavMapRules.DetailZoom, -0.045);

        public NavGround(Control owner, string cacheFolder, string source)
        {
            this.owner = owner;
            AgOpenGPS.Navigation.TileSetup.Prepare(cacheFolder);
            SetSource(source);
        }

        public bool HasMap { get { return wide.Texture != null || near.Texture != null; } }

        public void SetSource(string key)
        {
            key = NavMapRules.NormalizeSource(key);
            if (key == source) return;
            source = key;
            provider = key == NavMapRules.BingSatellite ? (GMapProvider)GMapProviders.BingSatelliteMap : GMapProviders.OpenStreetMap;
            Interlocked.Increment(ref generation);
            wide.Forget();   // the next fix makes squares from the new source (the old ones show until then)
            near.Forget();
        }

        /// <summary>Called with every GPS fix while the map is shown: starts new squares when they are needed.</summary>
        public void Update(Wgs84 position)
        {
            // the wide square first, so the driver gets a map soonest; the sharp one follows once that is there
            Start(wide, position);
            if (wide.Texture != null) Start(near, position);
        }

        private void Start(Layer layer, Wgs84 position)
        {
            GPoint pixel = provider.Projection.FromLatLngToPixel(position.Latitude, position.Longitude, layer.Zoom);
            long tileX = pixel.X / 256, tileY = pixel.Y / 256;
            if (layer.CentreX != long.MinValue && !NavMapRules.NeedsNewMosaic(layer.CentreX, layer.CentreY, tileX, tileY)) return;
            if (Interlocked.CompareExchange(ref layer.Building, 1, 0) != 0) return;

            layer.CentreX = tileX;
            layer.CentreY = tileY;
            GMapProvider p = provider;
            int gen = generation;
            ThreadPool.QueueUserWorkItem(_ => Build(layer, p, tileX, tileY, gen));
        }

        private void Build(Layer layer, GMapProvider p, long tileX, long tileY, int gen)
        {
            Bitmap bitmap = null;
            try
            {
                int n = NavMapRules.MosaicTiles, zoom = layer.Zoom;
                long x0 = NavMapRules.MosaicStart(tileX), y0 = NavMapRules.MosaicStart(tileY);
                bitmap = new Bitmap(256 * n, 256 * n);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.FromArgb(200, 203, 208));
                    for (int i = 0; i < n; i++)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            if (gen != generation) return;   // the source changed meanwhile: stop early
                            Exception error;
                            PureImage tile = GMaps.Instance.GetImageFrom(p, new GPoint(x0 + i, y0 + j), zoom, out error);
                            var image = tile as GMap.NET.WindowsForms.GMapImage;
                            if (image != null && image.Img != null) g.DrawImage(image.Img, i * 256, j * 256, 256, 256);
                            tile?.Dispose();
                        }
                    }
                }
                PointLatLng tl = p.Projection.FromPixelToLatLng(x0 * 256, y0 * 256, zoom);
                PointLatLng br = p.Projection.FromPixelToLatLng((x0 + n) * 256, (y0 + n) * 256, zoom);

                Bitmap done = bitmap;
                bitmap = null;
                owner.BeginInvoke(new Action(() =>
                {
                    if (gen != generation) { done.Dispose(); return; }
                    // the texture is made on the UI thread on its first use (Texture2D.Bind), where the GL context is
                    layer.Texture?.Dispose();
                    layer.Texture = new GeoTexture2D(done);
                    layer.Mipmapped = false;
                    layer.TopLeft = tl;
                    layer.BottomRight = br;
                }));
            }
            catch (Exception)
            {
                // no map this time: the next fix tries again
                layer.CentreX = layer.CentreY = long.MinValue;
            }
            finally
            {
                bitmap?.Dispose();
                Interlocked.Exchange(ref layer.Building, 0);
            }
        }

        /// <summary>Draws the squares on the ground (above the floor, below everything else). UI thread, GL context current.</summary>
        public void Draw(LocalPlane plane, bool night)
        {
            if (plane == null) return;
            // the map is light; at night it is dimmed so it does not dazzle the driver
            float shade = night ? 0.55f : 1f;
            GLW.SetColor(new ColorRgba(shade, shade, shade, 1f));
            wide.Draw(plane);
            near.Draw(plane);
        }

        /// <summary>Forgets the squares (a field opened): the next time the map is shown new ones are made.</summary>
        public void Clear()
        {
            Interlocked.Increment(ref generation);
            wide.Drop();
            near.Drop();
        }

        public void Dispose()
        {
            Clear();
        }

        // one square of tiles at one zoom level
        private sealed class Layer
        {
            public readonly int Zoom;
            private readonly double z;

            public GeoTexture2D Texture;
            public bool Mipmapped;
            public PointLatLng TopLeft, BottomRight;
            public long CentreX = long.MinValue, CentreY = long.MinValue;
            public int Building;

            public Layer(int zoom, double z)
            {
                Zoom = zoom;
                this.z = z;
            }

            public void Forget()
            {
                CentreX = CentreY = long.MinValue;
            }

            public void Drop()
            {
                Texture?.Dispose();
                Texture = null;
                Forget();
            }

            public void Draw(LocalPlane plane)
            {
                if (Texture == null) return;
                if (!Mipmapped)
                {
                    // smaller copies of the picture for the far part of the view: without them the distant map
                    // flickers and looks grainy; an older graphics driver without them just keeps the plain filter
                    Mipmapped = true;
                    try
                    {
                        Texture.Bind();
                        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                        GL.BindTexture(TextureTarget.Texture2D, 0);
                    }
                    catch (Exception)
                    {
                    }
                }
                GeoCoord tl = plane.ConvertWgs84ToGeoCoord(new Wgs84(TopLeft.Lat, TopLeft.Lng));
                GeoCoord br = plane.ConvertWgs84ToGeoCoord(new Wgs84(BottomRight.Lat, BottomRight.Lng));
                // GeoTexture2D.DrawZ takes easting in the Northing slot (as BingMapVisual does)
                Texture.DrawZ(new GeoCoord(tl.Easting, tl.Northing), new GeoCoord(br.Easting, br.Northing), z);
            }
        }
    }
}
