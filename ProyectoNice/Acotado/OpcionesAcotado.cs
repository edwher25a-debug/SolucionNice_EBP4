using ProyectoNice.Utils;

namespace ProyectoNice.Acotado
{
    /// <summary>Tipos, estilos y separaciones con que se acota. Las separaciones van en mm de papel.</summary>
    public sealed class OpcionesAcotado
    {
        public DimensionType? TipoCota { get; set; }
        public SpotDimensionType? TipoNivel { get; set; }
        public GraphicsStyle? EstiloMarcas { get; set; }
        public TipoVista Forzar { get; set; } = TipoVista.Automatico;

        public double PrimeraFila { get; set; } = 8;
        public double EntreFilas { get; set; } = 7;

        public bool Niveles { get; set; } = true;
        public bool Etiquetas { get; set; } = true;
        public bool Diametros { get; set; } = true;

        /// <summary>Valores del proyecto IDU-1739; si un nombre no existe, se usa el tipo por defecto de Revit.</summary>
        public static OpcionesAcotado PorDefecto(Document doc) => new OpcionesAcotado
        {
            TipoCota = TiposCota(doc).FirstOrDefault(t => t.Name == "PDE 3.8"),
            TipoNivel = TiposNivel(doc).FirstOrDefault(t => t.Name == "PED_ElevacionSeccion"),
            EstiloMarcas = MetodoDesarrollo.EstilosDeLinea(doc).FirstOrDefault(e => e.Name == "PED_INVISIBLE")
        };

        public static List<DimensionType> TiposCota(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Linear && !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name).ToList();

        public static List<SpotDimensionType> TiposNivel(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.SpotElevation && !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name).ToList();
    }
}
