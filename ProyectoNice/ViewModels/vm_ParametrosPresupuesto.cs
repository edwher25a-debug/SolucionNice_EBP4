using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using ProyectoNice.Models;
using ProyectoNice.Utils;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Compara los parametros de presupuesto (02_xx) de los elementos con el Excel del presupuesto detallado y
    ///     propone los cambios: cruza cada elemento con su item por el subcapitulo (o por el nombre dentro del mismo
    ///     capitulo si el subcapitulo no existe) y lista los campos vacios o distintos. Solo escribe los marcados.
    /// </summary>
    public sealed class vm_ParametrosPresupuesto : vm_Herramienta
    {
        //Datos de entrada
        public Document doc;

        //Excel
        private string archivo = "";
        public string Archivo
        {
            get => archivo;
            set => SetProperty(ref archivo, value);
        }

        private List<string> listaHojasCB = new List<string>();
        public List<string> ListaHojasCB
        {
            get => listaHojasCB;
            set => SetProperty(ref listaHojasCB, value);
        }
        private string hojaSeleccCB = "";
        public string HojaSeleccCB
        {
            get => hojaSeleccCB;
            set
            {
                if (!SetProperty(ref hojaSeleccCB, value)) return;
                LeerHoja();
            }
        }

        //Campos: columna del Excel y parametro de Revit
        public ObservableCollection<m_CampoPresupuesto> Campos { get; } = new ObservableCollection<m_CampoPresupuesto>();
        private List<m_ColumnaExcel> columnas = new List<m_ColumnaExcel>();
        public List<m_ColumnaExcel> Columnas
        {
            get => columnas;
            set => SetProperty(ref columnas, value);
        }

        private string resumenExcel = "Abra el Excel del presupuesto.";
        public string ResumenExcel
        {
            get => resumenExcel;
            set => SetProperty(ref resumenExcel, value);
        }

        //Alcance
        public bool SoloVistaActiva { get; set; }

        //Resultado
        public ObservableCollection<m_CambioParametro> Cambios { get; } = new ObservableCollection<m_CambioParametro>();
        private string resumenComparacion = "";
        public string ResumenComparacion
        {
            get => resumenComparacion;
            set => SetProperty(ref resumenComparacion, value);
        }

        //Botones
        public RelayCommand AbrirBT { get; set; }
        public RelayCommand CompararBT { get; set; }
        public RelayCommand MarcarVaciosBT { get; set; }
        public RelayCommand DesmarcarBT { get; set; }
        public RelayCommand ExportarBT { get; set; }
        public RelayCommand AplicarBT { get; set; }

        private List<string[]> filas = new List<string[]>();
        private int encabezado = -1;
        private List<m_ItemPresupuesto> items = new List<m_ItemPresupuesto>();

        //Constructor
        public vm_ParametrosPresupuesto(Document doc)
        {
            this.doc = doc;

            ObtenerPreData();
        }

        public void ObtenerPreData()
        {
            foreach (m_CampoPresupuesto campo in Presupuesto.CamposPorDefecto())
            {
                campo.PropertyChanged += CampoCambiado;
                Campos.Add(campo);
            }

            //Accion del Boton
            AbrirBT = new RelayCommand(Abrir);
            CompararBT = new RelayCommand(Comparar);
            MarcarVaciosBT = new RelayCommand(() => { foreach (var c in Cambios) c.Aplicar = c.Estado == EstadoVacio; });
            DesmarcarBT = new RelayCommand(() => { foreach (var c in Cambios) c.Aplicar = false; });
            ExportarBT = new RelayCommand(Exportar);
            AplicarBT = new RelayCommand(Validar);
        }

        public void Abrir()
        {
            var dialogo = new OpenFileDialog { Filter = "Presupuesto Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm" };
            if (dialogo.ShowDialog() != true) return;

            try
            {
                Archivo = dialogo.FileName;
                ListaHojasCB = LectorExcel.Hojas(Archivo);
                //La hoja que se llame "presupuesto detallado"; si no, la primera con "presupuesto"
                HojaSeleccCB = ListaHojasCB.FirstOrDefault(h => Presupuesto.Normalizar(h).Contains("DETALLAD"))
                               ?? ListaHojasCB.FirstOrDefault(h => Presupuesto.Normalizar(h).Contains("PRESUPUESTO"))
                               ?? ListaHojasCB.FirstOrDefault() ?? "";
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is System.Xml.XmlException || ex is InvalidOperationException)
            {
                ResumenExcel = $"No se pudo leer el Excel: {ex.Message}";
            }
        }

        private void LeerHoja()
        {
            if (Archivo.Length == 0 || HojaSeleccCB.Length == 0) return;
            try
            {
                filas = LectorExcel.LeerHoja(Archivo, HojaSeleccCB);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is System.Xml.XmlException || ex is InvalidOperationException)
            {
                ResumenExcel = $"No se pudo leer la hoja: {ex.Message}";
                return;
            }

            encabezado = Presupuesto.FilaEncabezado(filas, Campos);
            if (encabezado < 0)
            {
                Columnas = new List<m_ColumnaExcel>();
                ResumenExcel = $"No encontré la fila de encabezado en \"{HojaSeleccCB}\". Elija otra hoja.";
                return;
            }

            var lista = new List<m_ColumnaExcel> { new m_ColumnaExcel { Indice = -1, Texto = "(ninguna)" } };
            for (int c = 0; c < filas[encabezado].Length; c++)
                if (filas[encabezado][c].Length > 0) lista.Add(new m_ColumnaExcel { Indice = c, Texto = $"{Presupuesto.Letra(c)} · {Presupuesto.Limpiar(filas[encabezado][c])}" });
            Columnas = lista;

            leyendo = true;
            Presupuesto.DetectarColumnas(filas, encabezado, Campos);
            leyendo = false;
            ArmarItems();
        }

        private bool leyendo;
        private void CampoCambiado(object sender, PropertyChangedEventArgs e)
        {
            if (!leyendo && e.PropertyName == nameof(m_CampoPresupuesto.Columna)) ArmarItems();
        }

        private void ArmarItems()
        {
            var avisos = new List<string>();
            items = Presupuesto.Items(filas, encabezado, Campos.ToList(), avisos);
            int capitulos = items.Select(i => i.Valores[Presupuesto.Capitulo]).Distinct().Count(c => c.Length > 0);
            ResumenExcel = $"Hoja \"{HojaSeleccCB}\", encabezado en la fila {encabezado + 1}: {items.Count} ítems en {capitulos} capítulos." +
                           (avisos.Count > 0 ? $"\nRevise en el Excel: {string.Join(" ", avisos.Take(6))}" + (avisos.Count > 6 ? $" (y {avisos.Count - 6} más)" : "") : "");
            Aviso($"Parámetros desde presupuesto: {items.Count} ítems leídos.");
        }

        private const string EstadoVacio = "Vacío en el modelo";
        private const string EstadoDistinto = "Distinto al presupuesto";
        private const string EstadoSinItem = "Sin ítem en el presupuesto";

        public void Comparar()
        {
            Cambios.Clear();
            if (items.Count == 0)
            {
                Aviso("Parámetros desde presupuesto: primero abra el Excel del presupuesto.");
                return;
            }

            var porItem = items.GroupBy(i => i.Valores[Presupuesto.Subcapitulo]).ToDictionary(g => g.Key, g => g.First());
            var porNombre = items.GroupBy(i => Presupuesto.Normalizar(i.Valores[Presupuesto.Nombre])).ToDictionary(g => g.Key, g => g.ToList());
            m_CampoPresupuesto sub = Campos.First(c => c.Clave == Presupuesto.Subcapitulo);

            FilteredElementCollector colector = SoloVistaActiva && doc.ActiveView != null
                ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                : new FilteredElementCollector(doc);

            var grupos = new Dictionary<string, m_CambioParametro>();
            int revisados = 0, coinciden = 0, sinItem = 0;
            foreach (Element elemento in colector.WhereElementIsNotElementType())
            {
                if (elemento.Category == null || elemento.Category.CategoryType != CategoryType.Model) continue;
                ElementType? tipo = doc.GetElement(elemento.GetTypeId()) as ElementType;

                //Solo los elementos que tienen el parametro del subcapitulo
                if (Buscar(elemento, tipo, sub.Parametro).parametro == null) continue;
                revisados++;

                string valorSub = Valor(elemento, tipo, sub.Parametro);
                string valorNombre = Valor(elemento, tipo, Campos.First(c => c.Clave == Presupuesto.Nombre).Parametro);
                string valorCapitulo = Valor(elemento, tipo, Campos.First(c => c.Clave == Presupuesto.Capitulo).Parametro);
                (m_ItemPresupuesto? item, string via) = Cruzar(valorSub, valorNombre, valorCapitulo, porItem, porNombre);

                string familia = (elemento as FamilyInstance)?.Symbol.Family.Name ?? tipo?.FamilyName ?? "";
                if (item == null)
                {
                    sinItem++;
                    Agregar(grupos, EstadoSinItem, elemento, tipo, familia, sub.Parametro, false, valorSub, "", "", "");
                    continue;
                }

                bool igual = true;
                foreach (m_CampoPresupuesto campo in Campos)
                {
                    string ppto = item.Valores[campo.Clave];
                    (Parameter? parametro, bool enTipo) = Buscar(elemento, tipo, campo.Parametro);
                    if (parametro == null || ppto.Length == 0) continue;

                    string modelo = Texto(parametro);
                    string a = campo.Clave == Presupuesto.Subcapitulo ? Presupuesto.NormalizarItem(modelo) : Presupuesto.Normalizar(modelo);
                    string b = campo.Clave == Presupuesto.Subcapitulo ? ppto : Presupuesto.Normalizar(ppto);
                    if (a == b) continue;

                    igual = false;
                    bool vacio = a.Length == 0 || a == "N A" || a == "NA";
                    Agregar(grupos, vacio ? EstadoVacio : EstadoDistinto, elemento, tipo, familia, campo.Parametro, enTipo,
                        modelo, ppto, item.Valores[Presupuesto.Subcapitulo], via);
                }

                if (igual) coinciden++;
            }

            //Se proponen marcados solo los campos vacios; lo distinto lo decide el usuario
            foreach (m_CambioParametro cambio in grupos.Values
                         .OrderBy(c => c.Estado == EstadoVacio ? 0 : c.Estado == EstadoDistinto ? 1 : 2)
                         .ThenBy(c => c.Item).ThenBy(c => c.Familia).ThenBy(c => c.Parametro))
            {
                cambio.Aplicar = cambio.Estado == EstadoVacio;
                Cambios.Add(cambio);
            }

            ResumenComparacion = $"{revisados} elementos con \"{sub.Parametro}\": {coinciden} coinciden, {revisados - coinciden - sinItem} con cambios, " +
                                 $"{sinItem} sin ítem en el presupuesto. {Cambios.Count(c => c.Estado != EstadoSinItem)} cambios propuestos.";
            Aviso($"Parámetros desde presupuesto: {revisados} elementos revisados.");
        }

        //Item del elemento: por subcapitulo; si no existe, por nombre (primero dentro del mismo capitulo)
        private static (m_ItemPresupuesto?, string) Cruzar(string subcapitulo, string nombre, string capitulo,
            Dictionary<string, m_ItemPresupuesto> porItem, Dictionary<string, List<m_ItemPresupuesto>> porNombre)
        {
            string clave = Presupuesto.NormalizarItem(subcapitulo);
            if (clave.Length > 0 && porItem.TryGetValue(clave, out m_ItemPresupuesto? item)) return (item, "subcapítulo");

            if (!porNombre.TryGetValue(Presupuesto.Normalizar(nombre), out List<m_ItemPresupuesto>? candidatos)) return (null, "");
            List<m_ItemPresupuesto> mismoCapitulo = candidatos
                .Where(i => Presupuesto.Normalizar(i.Valores[Presupuesto.Capitulo]) == Presupuesto.Normalizar(capitulo)).ToList();
            if (mismoCapitulo.Count > 0) candidatos = mismoCapitulo;
            return (candidatos[0], candidatos.Count > 1 ? $"nombre ({candidatos.Count} ítems posibles)" : "nombre");
        }

        //Un cambio por familia, tipo, parametro y valores; si el parametro es de tipo se escribe una sola vez en el tipo
        private static void Agregar(Dictionary<string, m_CambioParametro> grupos, string estado, Element elemento, ElementType? tipo,
            string familia, string parametro, bool enTipo, string modelo, string ppto, string item, string via)
        {
            string clave = string.Join("|", estado, familia, tipo?.Name ?? "", parametro, enTipo, modelo, ppto, via);
            if (!grupos.TryGetValue(clave, out m_CambioParametro? cambio))
            {
                cambio = new m_CambioParametro
                {
                    Estado = estado, Categoria = elemento.Category.Name, Familia = familia, Tipo = tipo?.Name ?? "",
                    Parametro = parametro, EnTipo = enTipo ? "Tipo" : "Ejemplar",
                    ValorModelo = modelo, ValorPresupuesto = ppto, Item = item, CruzadoPor = via
                };
                grupos[clave] = cambio;
            }

            cambio.Elementos++;
            ElementId destino = enTipo && tipo != null ? tipo.Id : elemento.Id;
            if (!cambio.Destinos.Contains(destino)) cambio.Destinos.Add(destino);
        }

        //Parametro en el ejemplar o, si no, en su tipo
        private static (Parameter? parametro, bool enTipo) Buscar(Element elemento, ElementType? tipo, string nombre)
        {
            if (nombre.Length == 0) return (null, false);
            Parameter? enEjemplar = elemento.LookupParameter(nombre);
            if (enEjemplar != null) return (enEjemplar, false);
            Parameter? enTipo = tipo?.LookupParameter(nombre);
            return (enTipo, enTipo != null);
        }

        private static string Valor(Element elemento, ElementType? tipo, string nombre)
        {
            Parameter? parametro = Buscar(elemento, tipo, nombre).parametro;
            return parametro == null ? "" : Texto(parametro);
        }

        private static string Texto(Parameter parametro) =>
            (parametro.StorageType == StorageType.String ? parametro.AsString() : parametro.AsValueString()) ?? "";

        public void Exportar()
        {
            if (Cambios.Count == 0)
            {
                Aviso("Parámetros desde presupuesto: no hay resultados para exportar. Pulse Comparar.");
                return;
            }

            var dialogo = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Comparacion_presupuesto.csv" };
            if (dialogo.ShowDialog() != true) return;

            var texto = new StringBuilder();
            texto.AppendLine("Aplicar;Estado;Ítem;Cruzado por;Categoría;Familia;Tipo;Elementos;Parámetro;En;Valor modelo;Valor presupuesto");
            foreach (m_CambioParametro c in Cambios)
                texto.AppendLine(string.Join(";", new object[] { c.Aplicar ? "Sí" : "No", c.Estado, c.Item, c.CruzadoPor, c.Categoria, c.Familia, c.Tipo,
                    c.Elementos, c.Parametro, c.EnTipo, c.ValorModelo, c.ValorPresupuesto }.Select(v => "\"" + $"{v}".Replace("\"", "\"\"") + "\"")));
            File.WriteAllText(dialogo.FileName, texto.ToString(), new UTF8Encoding(true));
            Aviso($"Parámetros desde presupuesto: {Cambios.Count} filas exportadas.");
        }

        public void Validar()
        {
            if (!Cambios.Any(c => c.Aplicar && c.ValorPresupuesto.Length > 0))
            {
                Aviso("Parámetros desde presupuesto: marque al menos un cambio para aplicar.");
                return;
            }

            Ejecutar(Aplicar);
        }

        public void Aplicar()
        {
            List<m_CambioParametro> marcados = Cambios.Where(c => c.Aplicar && c.ValorPresupuesto.Length > 0).ToList();
            int escritos = 0, omitidos = 0;
            using (Transaction transaccion = new Transaction(doc, "Parámetros desde presupuesto"))
            {
                transaccion.Start();
                foreach (m_CambioParametro cambio in marcados)
                foreach (ElementId id in cambio.Destinos)
                {
                    Parameter? parametro = doc.GetElement(id)?.LookupParameter(cambio.Parametro);
                    if (parametro == null || parametro.IsReadOnly || parametro.StorageType != StorageType.String)
                    {
                        omitidos++;
                        continue;
                    }
                    parametro.Set(cambio.ValorPresupuesto);
                    escritos++;
                }
                transaccion.Commit();
            }

            foreach (m_CambioParametro cambio in marcados) Cambios.Remove(cambio);
            Aviso($"Parámetros desde presupuesto: {escritos} valores escritos" + (omitidos > 0 ? $", {omitidos} omitidos (parámetro de solo lectura o no es texto)." : "."));
        }
    }
}
