namespace ProyectoNice.Acotado
{
    /// <summary>Papel de un elemento en el dibujo de una estructura de puente.</summary>
    public enum Rol
    {
        Otro,
        Dado,
        Pilote,
        Columna,
        Viga,
        Apoyo,
        Box,
        Punto
    }

    /// <summary>Cara plana de un elemento, en coordenadas de la vista, con la referencia que sirve para acotarla.</summary>
    public sealed class CaraPlana
    {
        private readonly Lazy<List<(XYZ Modelo, XYZ Vista)>> centros;

        public CaraPlana(Reference? referencia, XYZ normal, XYZ punto, Func<List<(XYZ Modelo, XYZ Vista)>> centrosDeTriangulos)
        {
            Referencia = referencia;
            Normal = normal;
            Punto = punto;
            centros = new Lazy<List<(XYZ Modelo, XYZ Vista)>>(centrosDeTriangulos);
        }

        public Reference? Referencia { get; }
        public XYZ Normal { get; }
        public XYZ Punto { get; }

        /// <summary>
        ///     Punto de la cara (coordenadas del modelo) donde va su nivel: el centro de un triangulo de la cara,
        ///     de los mas cercanos al plano de la vista, en el extremo izquierdo o derecho. Revit exige que este dentro de la cara.
        /// </summary>
        public XYZ PuntoParaNivel(bool derecha)
        {
            IOrderedEnumerable<(XYZ Modelo, XYZ Vista)> cercanos = centros.Value.OrderBy(c => Math.Round(Math.Abs(c.Vista.Z), 2));
            return (derecha ? cercanos.ThenByDescending(c => c.Vista.X) : cercanos.ThenBy(c => c.Vista.X)).First().Modelo;
        }

        public double En(Eje eje) => Punto.En(eje);
        public bool PerpendicularA(Eje eje) => Math.Abs(Normal.En(eje)) > 0.999;
    }

    /// <summary>Elemento visible en la vista, con su papel, su contorno y lo que se puede acotar de el.</summary>
    public sealed class Pieza
    {
        /// <summary>Palabras del nombre de familia o tipo que deciden el papel. Ampliar segun el proyecto.</summary>
        private static readonly string[] PalabrasPilote = { "PILOT", "PILE" };
        private static readonly string[] PalabrasDado = { "DADO", "ZAPATA", "ENCEPADO" };
        private static readonly string[] PalabrasBox = { "BOX" };
        private static readonly string[] PalabrasNoBox = { "PERFIL" };
        private static readonly string[] PalabrasPunto = { "COORD" };

        private readonly Lienzo lienzo;
        private readonly Lazy<Forma> forma;

        private Pieza(Lienzo lienzo, Element elemento, Rol rol, Caja2D caja)
        {
            this.lienzo = lienzo;
            Elemento = elemento;
            Rol = rol;
            Caja = caja;
            forma = new Lazy<Forma>(() => Geometria.FormaDe(lienzo, elemento));
        }

        public Element Elemento { get; }
        public Rol Rol { get; }
        public Caja2D Caja { get; }

        /// <summary>Bordes circulares vistos de frente: para cotas de diametro.</summary>
        public IReadOnlyList<Reference> Arcos => forma.Value.Arcos;

        /// <summary>Elementos de la vista con un papel reconocido y visibles dentro del recorte.</summary>
        public static List<Pieza> DeLaVista(Lienzo lienzo) =>
            new FilteredElementCollector(lienzo.Doc, lienzo.Vista.Id).WhereElementIsNotElementType()
                .Select(e => (Elemento: e, Rol: RolDe(e), Caja: e.get_BoundingBox(lienzo.Vista)))
                .Where(x => x.Rol != Rol.Otro && x.Caja != null)
                .Select(x => new Pieza(lienzo, x.Elemento, x.Rol, Caja2D.De(lienzo.EsquinasDe(x.Caja)).Recortar(lienzo.Recorte)))
                .Where(p => !p.Caja.EsVacia)
                .ToList();

        /// <summary>Borde mas afuera de la pieza en ese eje; si no tiene ninguno acotable, el de su contorno.</summary>
        public Estacion Extremo(Eje eje, bool maximo)
        {
            List<Estacion> bordes = Bordes(eje).OrderBy(b => b.Posicion).ToList();
            if (bordes.Count == 0) return new Estacion(maximo ? Caja.Max(eje) : Caja.Min(eje), null);
            return maximo ? bordes.Last() : bordes.First();
        }

        /// <summary>Cara plana perpendicular al eje que queda mas afuera; nula si no hay.</summary>
        public CaraPlana? CaraExtrema(Eje eje, bool maximo)
        {
            List<CaraPlana> caras = forma.Value.Caras.Where(c => c.PerpendicularA(eje)).OrderBy(c => c.En(eje)).ToList();
            return caras.Count == 0 ? null : maximo ? caras.Last() : caras.First();
        }

        /// <summary>Plano central de la familia en ese eje; si la familia no lo tiene, el centro de su contorno.</summary>
        public Estacion Centro(Eje eje)
        {
            XYZ direccion = lienzo.Direccion(eje);
            double posicion = Elemento.Location is LocationPoint ubicacion && Math.Abs(direccion.Z) < 1e-6
                ? lienzo.AVista(ubicacion.Point).En(eje)
                : Caja.Centro(eje);

            Reference? plano = Elemento is FamilyInstance instancia
                ? new[]
                    {
                        (Tipo: FamilyInstanceReferenceType.CenterLeftRight, Normal: instancia.HandOrientation),
                        (Tipo: FamilyInstanceReferenceType.CenterFrontBack, Normal: instancia.FacingOrientation),
                        (Tipo: FamilyInstanceReferenceType.CenterElevation, Normal: XYZ.BasisZ)
                    }
                    .Where(c => Math.Abs(c.Normal.DotProduct(direccion)) > 0.999)
                    .SelectMany(c => instancia.GetReferences(c.Tipo))
                    .FirstOrDefault()
                : null;

            return new Estacion(posicion, plano);
        }

        //Caras planas perpendiculares, aristas vistas de punta y siluetas de cilindros. Las aristas cubren los
        //bordes sin cara paralela: los extremos inclinados de una viga cabezal o la corona inclinada de un espaldar
        private IEnumerable<Estacion> Bordes(Eje eje)
        {
            Forma f = forma.Value;
            return f.Caras
                .Where(c => c.PerpendicularA(eje))
                .Select(c => new Estacion(c.En(eje), c.Referencia))
                .Concat(f.AristasDePunta.Select(a => new Estacion(a.Punto.En(eje), a.Referencia)))
                .Concat(f.Cilindros
                    .Where(c => Math.Abs(c.Eje.En(eje)) < 0.01)
                    .SelectMany(c => new[] { c.Centro.En(eje) - c.Radio, c.Centro.En(eje) + c.Radio })
                    .Select(posicion => new Estacion(posicion, null)));
        }

        //Familias que agrupan otras (CIMENTACION-T1 con dado y pilotes): se acota lo que contienen, no el grupo
        private static bool EsSoloContenedor(Element elemento) =>
            elemento is FamilyInstance instancia && instancia.GetSubComponentIds().Count > 0;

        private static Rol RolDe(Element elemento)
        {
            if (elemento.Category == null || elemento is CurveElement || EsSoloContenedor(elemento)) return Rol.Otro;

            string nombre = elemento is FamilyInstance instancia
                ? $"{instancia.Symbol.FamilyName} {instancia.Symbol.Name}".ToUpperInvariant()
                : elemento.Name.ToUpperInvariant();
            bool Dice(string[] palabras) => palabras.Any(nombre.Contains);

            switch (elemento.Category.BuiltInCategory)
            {
                case BuiltInCategory.OST_StructuralFoundation: return Dice(PalabrasPilote) ? Rol.Pilote : Dice(PalabrasDado) ? Rol.Dado : Rol.Otro;
                case BuiltInCategory.OST_StructuralColumns: return Dice(PalabrasPilote) ? Rol.Pilote : Rol.Columna;
                case BuiltInCategory.OST_StructuralFraming: return Rol.Viga;
                case BuiltInCategory.OST_StructConnections: return Rol.Apoyo;
                case BuiltInCategory.OST_GenericModel:
                    return Dice(PalabrasBox) && !Dice(PalabrasNoBox) ? Rol.Box : Dice(PalabrasPunto) ? Rol.Punto : Rol.Otro;
                default: return Rol.Otro;
            }
        }
    }
}
