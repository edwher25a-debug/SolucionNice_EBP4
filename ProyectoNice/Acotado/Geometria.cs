namespace ProyectoNice.Acotado
{
    /// <summary>Lo que se puede acotar de un elemento en una vista, en coordenadas de la vista.</summary>
    public sealed class Forma
    {
        public List<CaraPlana> Caras { get; } = new List<CaraPlana>();

        /// <summary>Aristas rectas vistas de punta: en la vista son un punto al que se puede acotar.</summary>
        public List<(XYZ Punto, Reference? Referencia)> AristasDePunta { get; } = new List<(XYZ, Reference?)>();

        /// <summary>Cilindros (columnas, pilotes): centro, direccion del eje y radio.</summary>
        public List<(XYZ Centro, XYZ Eje, double Radio)> Cilindros { get; } = new List<(XYZ, XYZ, double)>();

        /// <summary>Bordes circulares vistos de frente: para cotas de diametro.</summary>
        public List<Reference> Arcos { get; } = new List<Reference>();
    }

    /// <summary>Lee la geometria de un elemento con referencias que Revit acepta para acotar en la vista.</summary>
    internal static class Geometria
    {
        public static Forma FormaDe(Lienzo lienzo, Element elemento)
        {
            var forma = new Forma();
            foreach (SolidoUbicado s in Solidos(lienzo, elemento))
            {
                forma.Caras.AddRange(s.Solido.Faces.OfType<PlanarFace>().Select(cara => new CaraPlana(
                    s.Referencia(cara.Reference),
                    lienzo.VectorAVista(s.AlModelo.OfVector(cara.FaceNormal)),
                    lienzo.AVista(s.AlModelo.OfPoint(cara.Origin)),
                    () => CentrosDeTriangulos(lienzo, cara.Triangulate(), s.AlModelo))));

                forma.Cilindros.AddRange(s.Solido.Faces.OfType<CylindricalFace>().Select(cilindro => (
                    lienzo.AVista(s.AlModelo.OfPoint(cilindro.Origin)),
                    lienzo.VectorAVista(s.AlModelo.OfVector(cilindro.Axis)),
                    cilindro.get_Radius(0).GetLength())));

                List<(Edge Arista, Curve Curva)> aristas = s.Solido.Edges.Cast<Edge>().Select(a => (a, a.AsCurve())).ToList();
                //Solo aristas exactamente de punta: una inclinada aunque sea un poco no sirve de referencia
                forma.AristasDePunta.AddRange(aristas
                    .Where(a => a.Curva is Line linea && Math.Abs(lienzo.VectorAVista(s.AlModelo.OfVector(linea.Direction)).Z) > 1 - 1e-9)
                    .Select(a => (lienzo.AVista(s.AlModelo.OfPoint(a.Curva.GetEndPoint(0))), s.Referencia(a.Arista.Reference))));
                forma.Arcos.AddRange(aristas
                    .Where(a => a.Curva is Arc arco && Math.Abs(lienzo.VectorAVista(s.AlModelo.OfVector(arco.Normal)).Z) > 0.999)
                    .Select(a => s.Referencia(a.Arista.Reference))
                    .OfType<Reference>());
            }

            return forma;
        }

        //Centros de los triangulos de la cara: puntos que estan dentro de ella, en el modelo y en la vista
        private static List<(XYZ Modelo, XYZ Vista)> CentrosDeTriangulos(Lienzo lienzo, Mesh malla, Transform alModelo) =>
            Enumerable.Range(0, malla.NumTriangles)
                .Select(malla.get_Triangle)
                .Select(t => alModelo.OfPoint((t.get_Vertex(0) + t.get_Vertex(1) + t.get_Vertex(2)) / 3))
                .Select(p => (p, lienzo.AVista(p)))
                .ToList();

        private sealed class SolidoUbicado
        {
            public SolidoUbicado(Solid solido, Transform alModelo, Func<Reference?, Reference?> referencia)
            {
                Solido = solido;
                AlModelo = alModelo;
                Referencia = referencia;
            }

            public Solid Solido { get; }
            public Transform AlModelo { get; }

            /// <summary>Convierte la referencia de la geometria en una que sirva para acotar la instancia.</summary>
            public Func<Reference?, Reference?> Referencia { get; }
        }

        private static IEnumerable<SolidoUbicado> Solidos(Lienzo lienzo, Element elemento)
        {
            GeometryElement? geometria = elemento.get_Geometry(new Options { ComputeReferences = true, View = lienzo.Vista });
            return geometria == null
                ? Enumerable.Empty<SolidoUbicado>()
                : Recorrer(geometria, Transform.Identity, referencia => referencia, r => DeInstancia(lienzo.Doc, elemento, r));
        }

        //Primer nivel: referencias propias. Dentro de la familia: referencias del simbolo atadas a la instancia.
        //Familias anidadas sin elemento propio: sin referencia; se acotan con marcas
        private static IEnumerable<SolidoUbicado> Recorrer(GeometryElement geometria, Transform alModelo,
            Func<Reference?, Reference?> referencia, Func<Reference?, Reference?> dentro) =>
            geometria.SelectMany(objeto => objeto switch
            {
                Solid solido when solido.Faces.Size > 0 => new[] { new SolidoUbicado(solido, alModelo, referencia) },
                GeometryInstance instancia => Recorrer(instancia.GetSymbolGeometry(), alModelo.Multiply(instancia.Transform), dentro, _ => null),
                _ => Enumerable.Empty<SolidoUbicado>()
            });

        private static Reference? DeInstancia(Document doc, Element elemento, Reference? delSimbolo)
        {
            if (delSimbolo == null) return null;
            try
            {
                return Reference.ParseFromStableRepresentation(doc, $"{elemento.UniqueId}:0:INSTANCE:{delSimbolo.ConvertToStableRepresentation(doc)}");
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }
    }
}
