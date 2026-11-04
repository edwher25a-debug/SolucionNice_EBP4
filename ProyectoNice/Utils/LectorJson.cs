using System.Globalization;
using System.IO;
using System.Text;
using ProyectoNice.Models;

namespace ProyectoNice.Utils
{
    /// <summary>
    ///     Lector de los manifiestos que escribe el lado de AutoCAD.
    ///     Hecho a mano a proposito: System.Text.Json obligaria a un paquete nuevo y en Revit
    ///     los paquetes compartidos con otros complementos son una fuente conocida de conflictos
    ///     de version (igual que paso con Nice3point y CommunityToolkit). El esquema lo controlamos
    ///     nosotros, asi que un parser de cien lineas sale mas barato que una dependencia.
    /// </summary>
    public static class LectorJson
    {
        //=== Lectura del manifiesto ===

        public static m_PlanoCAD LeerPlano(string rutaManifiesto)
        {
            object raiz = Analizar(File.ReadAllText(rutaManifiesto, Encoding.UTF8));
            var mapa = raiz as Dictionary<string, object>;
            if (mapa == null) throw new InvalidDataException("el manifiesto no es un objeto JSON");

            var plano = new m_PlanoCAD
            {
                Codigo = Texto(mapa, "codigo"),
                Titulo = Texto(mapa, "titulo"),
                ArchivoOrigen = Texto(mapa, "archivoOrigen"),
                Layout = Texto(mapa, "layout"),
                PapelAnchoMm = Numero(mapa, "papelAnchoMm"),
                PapelAltoMm = Numero(mapa, "papelAltoMm"),
                RutaManifiesto = rutaManifiesto,
                Carpeta = Path.GetDirectoryName(rutaManifiesto) ?? ""
            };

            if (plano.Codigo == "") throw new InvalidDataException("el manifiesto no trae codigo");

            foreach (object aviso in Lista(mapa, "avisos"))
                plano.Avisos.Add(aviso as string ?? "");

            foreach (object elemento in Lista(mapa, "vistas"))
            {
                var m = elemento as Dictionary<string, object>;
                if (m == null) continue;

                var vista = new m_VistaCAD
                {
                    Nombre = Texto(m, "nombre"),
                    Handle = Texto(m, "handle"),
                    Dwg = Texto(m, "dwg"),
                    EscalaDenominador = (int)Math.Round(Numero(m, "escalaDenominador")),
                    CustomScale = Numero(m, "customScale"),
                    VentanaAnchoM = Numero(m, "ventanaAnchoM"),
                    VentanaAltoM = Numero(m, "ventanaAltoM"),
                    VentanaCentroX = Numero(m, "ventanaCentroX"),
                    VentanaCentroY = Numero(m, "ventanaCentroY"),
                    PapelCentroXMm = Numero(m, "papelCentroXMm"),
                    PapelCentroYMm = Numero(m, "papelCentroYMm")
                };

                foreach (object t in Lista(m, "textos"))
                {
                    var mt = t as Dictionary<string, object>;
                    if (mt == null) continue;
                    vista.Textos.Add(new m_TextoCAD
                    {
                        Texto = Texto(mt, "texto"),
                        XM = Numero(mt, "xM"),
                        YM = Numero(mt, "yM"),
                        AlturaMm = Numero(mt, "alturaMm"),
                        RotacionGrados = Numero(mt, "rotacionGrados"),
                        Capa = Texto(mt, "capa")
                    });
                }

                plano.Vistas.Add(vista);
            }

            return plano;
        }

        private static string Texto(Dictionary<string, object> mapa, string clave) =>
            mapa.TryGetValue(clave, out object valor) ? valor as string ?? "" : "";

        private static double Numero(Dictionary<string, object> mapa, string clave) =>
            mapa.TryGetValue(clave, out object valor) && valor is double d ? d : 0.0;

        private static List<object> Lista(Dictionary<string, object> mapa, string clave) =>
            mapa.TryGetValue(clave, out object valor) && valor is List<object> l ? l : new List<object>();

        //=== Parser ===

        private static object Analizar(string texto)
        {
            int i = 0;
            object valor = LeerValor(texto, ref i);
            SaltarBlancos(texto, ref i);
            if (i < texto.Length) throw new InvalidDataException($"sobra texto despues del JSON, posicion {i}");
            return valor;
        }

        private static object LeerValor(string s, ref int i)
        {
            SaltarBlancos(s, ref i);
            if (i >= s.Length) throw new InvalidDataException("JSON incompleto");

            switch (s[i])
            {
                case '{': return LeerObjeto(s, ref i);
                case '[': return LeerArreglo(s, ref i);
                case '"': return LeerCadena(s, ref i);
                case 't': Esperar(s, ref i, "true"); return true;
                case 'f': Esperar(s, ref i, "false"); return false;
                case 'n': Esperar(s, ref i, "null"); return null;
                default: return LeerNumero(s, ref i);
            }
        }

        private static Dictionary<string, object> LeerObjeto(string s, ref int i)
        {
            var mapa = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++;   //{
            SaltarBlancos(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return mapa; }

            while (true)
            {
                SaltarBlancos(s, ref i);
                string clave = LeerCadena(s, ref i);
                SaltarBlancos(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new InvalidDataException($"falta ':' en la posicion {i}");
                i++;
                mapa[clave] = LeerValor(s, ref i);
                SaltarBlancos(s, ref i);
                if (i >= s.Length) throw new InvalidDataException("objeto JSON sin cerrar");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return mapa; }
                throw new InvalidDataException($"se esperaba ',' o '}}' en la posicion {i}");
            }
        }

        private static List<object> LeerArreglo(string s, ref int i)
        {
            var lista = new List<object>();
            i++;   //[
            SaltarBlancos(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return lista; }

            while (true)
            {
                lista.Add(LeerValor(s, ref i));
                SaltarBlancos(s, ref i);
                if (i >= s.Length) throw new InvalidDataException("arreglo JSON sin cerrar");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return lista; }
                throw new InvalidDataException($"se esperaba ',' o ']' en la posicion {i}");
            }
        }

        private static string LeerCadena(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new InvalidDataException($"se esperaba una cadena en la posicion {i}");
            i++;
            var sb = new StringBuilder();

            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\') { sb.Append(c); continue; }

                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new InvalidDataException("escape \\u incompleto");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new InvalidDataException($"escape desconocido \\{e}");
                }
            }

            throw new InvalidDataException("cadena JSON sin cerrar");
        }

        private static double LeerNumero(string s, ref int i)
        {
            int inicio = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;

            string crudo = s.Substring(inicio, i - inicio);
            //Invariante siempre: el manifiesto lo escribe AutoCAD con punto decimal
            if (!double.TryParse(crudo, NumberStyles.Float, CultureInfo.InvariantCulture, out double valor))
                throw new InvalidDataException($"numero no valido \"{crudo}\" en la posicion {inicio}");

            return valor;
        }

        private static void Esperar(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new InvalidDataException($"se esperaba \"{literal}\" en la posicion {i}");
            i += literal.Length;
        }

        private static void SaltarBlancos(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }
    }
}
