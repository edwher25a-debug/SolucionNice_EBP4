namespace ProyectoNice.Models
{
    //Item del presupuesto detallado (una fila con unidad), con el capitulo que tiene encima
    public class m_ItemPresupuesto
    {
        public int Fila { get; set; }
        public Dictionary<string, string> Valores { get; } = new Dictionary<string, string>();
    }

    //Campo del presupuesto: de que columna del Excel sale y en que parametro de Revit se escribe
    public class m_CampoPresupuesto : ObservableObject
    {
        public string Clave { get; set; } = "";
        public string Nombre { get; set; } = "";
        public string[] Sinonimos { get; set; } = Array.Empty<string>();

        private string parametro = "";
        public string Parametro
        {
            get => parametro;
            set => SetProperty(ref parametro, value);
        }

        //Columna del Excel (base 0, -1 = sin columna); el capitulo no tiene columna, sale de las filas de capitulo
        private int columna = -1;
        public int Columna
        {
            get => columna;
            set => SetProperty(ref columna, value);
        }
    }

    //Columna del Excel para elegir en la lista
    public class m_ColumnaExcel
    {
        public int Indice { get; set; }
        public string Texto { get; set; } = "";
    }

    //Cambio propuesto: un parametro de un grupo de elementos (o de un tipo) que no coincide con el presupuesto
    public class m_CambioParametro : ObservableObject
    {
        private bool aplicar;
        public bool Aplicar
        {
            get => aplicar;
            set => SetProperty(ref aplicar, value);
        }

        public string Estado { get; set; } = "";
        public string Categoria { get; set; } = "";
        public string Familia { get; set; } = "";
        public string Tipo { get; set; } = "";
        public int Elementos { get; set; }
        public string Parametro { get; set; } = "";
        public string EnTipo { get; set; } = "";
        public string ValorModelo { get; set; } = "";
        public string ValorPresupuesto { get; set; } = "";
        public string Item { get; set; } = "";
        public string CruzadoPor { get; set; } = "";

        //Elementos (o el tipo) donde se escribe el valor
        public List<ElementId> Destinos { get; } = new List<ElementId>();
    }
}
