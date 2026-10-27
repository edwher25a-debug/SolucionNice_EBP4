using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProyectoNice.Models;

namespace ProyectoNice.Utils
{
    /// <summary>
    ///     Lee la hoja de presupuesto detallado: busca la fila de encabezado, la columna de cada campo (por el nombre del
    ///     encabezado y por lo que contiene) y arma la lista de items con su capitulo (las filas sin unidad son capitulos)
    /// </summary>
    public static class Presupuesto
    {
        public const string Codigo = "Codigo", Capitulo = "Capitulo", Subcapitulo = "Subcapitulo", Nombre = "Nombre", Unidad = "Unidad";

        private static readonly string[] unidades =
            { "M", "M2", "M3", "ML", "KG", "UND", "UN", "U", "GL", "UNIDAD", "TON", "T", "L", "HA", "KM", "DIA", "MES", "M3KM" };

        //Campos del estandar 02_xx con los nombres de encabezado mas comunes
        public static List<m_CampoPresupuesto> CamposPorDefecto() => new List<m_CampoPresupuesto>
        {
            new m_CampoPresupuesto { Clave = Codigo, Nombre = "Código / especificación", Parametro = "02_01_CÓDIGO_BBDD",
                Sinonimos = new[] { "CODIGO BBDD", "ESPECIFICACION", "ESPECIFICACIONES", "NORMA", "ARTICULO", "REFERENCIA", "CODIGO" } },
            new m_CampoPresupuesto { Clave = Capitulo, Nombre = "Capítulo", Parametro = "02_02_CAPITULO", Sinonimos = new[] { "CAPITULO" } },
            new m_CampoPresupuesto { Clave = Subcapitulo, Nombre = "Subcapítulo / ítem", Parametro = "02_03_SUBCAPITULO",
                Sinonimos = new[] { "SUBCAPITULO", "ITEM", "ITEMS", "NUMERAL", "NO", "N" } },
            new m_CampoPresupuesto { Clave = Nombre, Nombre = "Nombre / descripción", Parametro = "02_04_NOMBRE",
                Sinonimos = new[] { "NOMBRE", "DESCRIPCION", "DESCRIPCION DEL ITEM", "ACTIVIDAD", "CONCEPTO" } },
            new m_CampoPresupuesto { Clave = Unidad, Nombre = "Unidad de medida", Parametro = "02_05_UNIDAD_MEDIDA",
                Sinonimos = new[] { "UNIDAD MEDIDA", "UNIDAD", "UND", "UN", "UM" } }
        };

        //Fila de encabezado: entre las primeras 30, la que mas se parece a los nombres de los campos
        public static int FilaEncabezado(List<string[]> filas, IEnumerable<m_CampoPresupuesto> campos)
        {
            HashSet<string> nombres = new HashSet<string>(campos.SelectMany(c => c.Sinonimos));
            int mejor = -1, puntos = 0;
            for (int f = 0; f < Math.Min(30, filas.Count); f++)
            {
                int n = filas[f].Count(c => nombres.Contains(Normalizar(c)));
                if (n > puntos) { puntos = n; mejor = f; }
            }
            return mejor;
        }

        //Columna de cada campo: puntaje por el encabezado + por el contenido de las filas de abajo
        public static void DetectarColumnas(List<string[]> filas, int encabezado, IEnumerable<m_CampoPresupuesto> campos)
        {
            if (encabezado < 0) return;
            int columnas = filas[encabezado].Length;
            foreach (m_CampoPresupuesto campo in campos)
            {
                campo.Columna = -1;
                if (campo.Clave == Capitulo) continue;

                double mejor = 0;
                for (int c = 0; c < columnas; c++)
                {
                    string encab = Normalizar(filas[encabezado][c]);
                    double puntos = 0;
                    int posicion = Array.IndexOf(campo.Sinonimos, encab);
                    if (posicion >= 0) puntos += 60 - posicion;

                    List<string> muestra = filas.Skip(encabezado + 1).Take(60).Select(f => c < f.Length ? f[c].Trim() : "").Where(t => t.Length > 0).ToList();
                    if (muestra.Count == 0) continue;
                    puntos += 40 * Contenido(campo.Clave, muestra);

                    if (puntos > mejor) { mejor = puntos; campo.Columna = c; }
                }
            }
        }

        //Que tanto se parece el contenido de una columna al campo (0 a 1)
        private static double Contenido(string clave, List<string> muestra)
        {
            switch (clave)
            {
                case Unidad: return muestra.Count(EsUnidad) / (double)muestra.Count;
                case Subcapitulo: return muestra.Count(t => Regex.IsMatch(t, @"^\d+([.,]\d+)+[.,]?$")) / (double)muestra.Count;
                case Nombre: return Math.Min(1, muestra.Average(t => t.Length) / 40);
                case Codigo:
                    return muestra.Count(t => Regex.IsMatch(t, @"(?i)art\.|esp\.|invias|norma|\d{3}") && !Regex.IsMatch(t, @"^[\d.,]+$")) / (double)muestra.Count;
                default: return 0;
            }
        }

        /// <summary>
        ///     Items de la hoja: cada fila con subcapitulo y unidad; las filas sin unidad que empiezan por "CAPITULO"
        ///     dan el capitulo a los items de abajo
        /// </summary>
        public static List<m_ItemPresupuesto> Items(List<string[]> filas, int encabezado, List<m_CampoPresupuesto> campos, List<string> avisos)
        {
            m_CampoPresupuesto sub = campos.First(c => c.Clave == Subcapitulo);
            m_CampoPresupuesto uni = campos.First(c => c.Clave == Unidad);
            var items = new List<m_ItemPresupuesto>();
            if (encabezado < 0 || sub.Columna < 0 || uni.Columna < 0) return items;

            string capitulo = "";
            for (int f = encabezado + 1; f < filas.Count; f++)
            {
                string item = Celda(filas[f], sub.Columna);
                if (item.Length == 0) continue;
                if (Celda(filas[f], uni.Columna).Length == 0)
                {
                    if (Normalizar(item).StartsWith("CAPITULO")) capitulo = Limpiar(item);
                    continue;
                }

                if (Regex.IsMatch(item, @"[.,]$")) avisos.Add($"Fila {f + 1}: ítem mal escrito \"{item}\".");
                var nuevo = new m_ItemPresupuesto { Fila = f + 1 };
                foreach (m_CampoPresupuesto campo in campos)
                {
                    string valor = campo.Clave == Capitulo ? capitulo : Limpiar(Celda(filas[f], campo.Columna));
                    if (campo.Clave == Subcapitulo) valor = NormalizarItem(valor);
                    if (campo.Clave == Unidad) valor = valor.ToUpperInvariant();
                    nuevo.Valores[campo.Clave] = valor;
                }

                if (nuevo.Valores.TryGetValue(Codigo, out string? cod) && cod.Length == 0) avisos.Add($"Fila {f + 1}: ítem {nuevo.Valores[Subcapitulo]} sin código o especificación.");
                items.Add(nuevo);
            }

            foreach (var repetido in items.GroupBy(i => i.Valores[Subcapitulo]).Where(g => g.Count() > 1))
                avisos.Add($"El ítem {repetido.Key} aparece {repetido.Count()} veces (filas {string.Join(", ", repetido.Select(i => i.Fila))}).");
            return items;
        }

        //Item con coma decimal y sin separador final: "5.1" y "5,1." -> "5,1"
        public static string NormalizarItem(string item) => item.Trim().Replace('.', ',').TrimEnd(',');

        public static bool EsUnidad(string texto) => unidades.Contains(Normalizar(texto).Replace(" ", ""));

        //Mayusculas, sin tildes, sin prefijo "02_04_" ni simbolos: para comparar textos
        public static string Normalizar(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "";
            string sinTildes = new string(texto.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
            sinTildes = Regex.Replace(sinTildes, @"^\d+(_\d+)*_", "");
            return Regex.Replace(Regex.Replace(sinTildes, "[^A-Za-z0-9]", " "), @"\s+", " ").Trim().ToUpperInvariant();
        }

        //Espacios repetidos y saltos de linea del Excel
        public static string Limpiar(string texto) => Regex.Replace(texto ?? "", @"\s+", " ").Trim();

        private static string Celda(string[] fila, int columna) => columna >= 0 && columna < fila.Length ? fila[columna].Trim() : "";

        //Letra de columna de Excel
        public static string Letra(int columna)
        {
            string letra = "";
            for (int c = columna + 1; c > 0; c = (c - 1) / 26) letra = (char)('A' + (c - 1) % 26) + letra;
            return letra;
        }
    }
}
