using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProyectoNice.Utils
{
    public class MetodoUnidades
    {
        public static double Pies3AMetros3(double pies3)
        {
            double metros3 = pies3 * 0.3048 * 0.3048 * 0.3048;
            return metros3;
        }
        public static double PiesAMetros(double pies)
        {
            double metros = pies * 0.3048;
            return metros;
        }

    }
}
