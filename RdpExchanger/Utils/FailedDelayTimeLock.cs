using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RdpExchanger
{
    public class FailedDelayTimeLock
    {
        public DateTime lastSuccessTime;

        public float ProcessWaiting = 0.1f;

        public FailedDelayTimeLock()
        {
            lastSuccessTime = DateTime.Now;
        }

        public void Success()
        {
            lastSuccessTime = DateTime.Now;
        }

        public void Wait()
        {
            if (FailedDuration > ProcessWaiting)
            {
                Thread.Sleep(1);
            }
        }

        public float FailedDuration => (float)(DateTime.Now - lastSuccessTime).TotalSeconds;
    }
}
