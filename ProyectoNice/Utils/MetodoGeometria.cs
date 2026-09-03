using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProyectoNice.Utils
{
    public class MetodoGeometria
    {
        public static List<Solid> ObtenerSolidosdeLista (List<Element> listaElem)
        {
            var listaFinalSolidos = new List<Solid>();
            foreach (var element in listaElem)
            {
                List<Solid> solidosdeUno = ObtenerSolidos(element);
                if (solidosdeUno.Count == 1)
                {
                    listaFinalSolidos.Add(solidosdeUno.FirstOrDefault());
                }
                else
                {
                    foreach (var sol in solidosdeUno)
                    {
                        listaFinalSolidos.Add(sol);
                    }
                }
                
              

            }

            return listaFinalSolidos;
        }

        public static List<Solid> ObtenerSolidos (Element elem)
        {
            var listaSolidos = new List<Solid>();
            Options ops = new Options ();
            GeometryElement ge = elem.get_Geometry(ops);

            foreach (GeometryObject go in ge)
            {
                if (go is Solid)
                {
                    Solid solido = (Solid)go;
                    if (solido.Volume > 0)
                    {
                        listaSolidos.Add(solido);
                    }
                }
            }
            return listaSolidos;
        }

    }
}
