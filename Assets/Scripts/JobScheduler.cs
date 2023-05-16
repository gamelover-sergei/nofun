using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Nofun
{
    public class JobScheduler : MonoBehaviour
    {
        private class Job
        {
            public Action caller;
            public AutoResetEvent evt;

            public Job(Action caller, AutoResetEvent waiter = null)
            {
                this.caller = caller;
                this.evt = waiter;
            }
        };

        public static JobScheduler Instance { get; private set; }
        public static bool Paused { get; set; }
        private Thread unityThread;
        private Queue<Job> jobs;

        private void Start()
        {
            unityThread = Thread.CurrentThread;
            Paused = false;
            jobs = new();

            Instance = this;
        }

        private void Update()
        {
            if (Paused)
            {
                return;
            }

            lock (jobs)
            {
                while (jobs.Count != 0)
                {
                    Job job = jobs.Dequeue();
                    job.caller();

                    if (job.evt != null)
                    {
                        job.evt.Set();
                    }
                }
            }   
        }

        public void RunOnUnityThread(Action act)
        {
            if (Thread.CurrentThread != unityThread)
            {
                lock (jobs)
                {
                    jobs.Enqueue(new Job(act));
                }
            }
            else
            {
                act();
            }
        }

        public void RunOnUnityThreadSync(Action act)
        {
            if (Thread.CurrentThread != unityThread)
            {
                AutoResetEvent evt = new AutoResetEvent(false);

                lock (jobs)
                {
                    jobs.Enqueue(new Job(act, evt));
                }

                evt.WaitOne();
            }
            else
            {
                act();
            }
        }
    }
}