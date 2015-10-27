﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.ServiceFabric.Data.Collections;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IoTProcessorManagement.Common
{
    public partial class WorkManager<Handler, Wi> where Handler : IWorkItemHandler<Wi>, new()
                                                  where Wi : IWorkItem
    { 

        /// <summary>
        /// part of Work Manager that handles actual dequeue process
        /// </summary>
        /// <typeparam name="H"></typeparam>
        /// <typeparam name="W"></typeparam>
    private  class WorkExecuter<H, W> where H : IWorkItemHandler<W>, new()
                                             where W : IWorkItem
    {
        private WorkManager<Handler, Wi> m_WorkManager;
        private Task m_Task;
        private bool m_KeepWorking = true;
        private bool m_Pause = false;
        internal readonly string m_WorkerExecuterId = Guid.NewGuid().ToString();

        public WorkExecuter(WorkManager<Handler, Wi> workManager)
        {
            m_WorkManager = workManager;
        }

        public void  Start()
        {
             m_Task = Task.Run(() => workloop());
        }
        public void Pause()
        {
                m_Pause = true;
        }

        public void Resume()
        {
                m_Pause = false;
        }
        public void Stop()
        {
           m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Worker {0} signled to stop", m_WorkerExecuterId));
                m_Pause       = false;
                m_KeepWorking = false;
                m_Task.Wait();

           m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Worker {0} stopped", m_WorkerExecuterId));
         }

        private void workloop()
        {
                try
                {
                    workLoopAsync().Wait();
                }
                catch(AggregateException aex)
                {
                    var ae = aex.Flatten();
                    m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Executer encountered a fatel error and will exit Error:{0} StackTrace{1}", ae.GetCombinedExceptionMessage(), ae.GetCombinedExceptionStackTrace()));
                    throw;
                }
        }

         private void leaveQ(string qName, bool bRemoveFromEmptySuspects)
         {
                long LastCheckedVal;
                m_WorkManager.m_QueueManager.LeaveQueueAsync(qName);
                if(bRemoveFromEmptySuspects)
                    m_WorkManager.m_QueueManager.m_SuspectedEmptyQueues.TryRemove(qName, out LastCheckedVal);

         }
        private async Task FinilizeQueueWork(int NumOfDeqeuedMsgs, string qName, IReliableQueue<Wi> q)
        {
                long LastCheckedVal;

                if (null == q)
                {
                    // this executer tried to get q and failed. 
                    // typically this happens when # of executers > q
                    // check for decrease workers 
                    m_WorkManager.m_DeferedTaskExec.AddWork(m_WorkManager.TryDecreaseExecuters);
                    return;
                }

                var bMoreMessages = NumOfDeqeuedMsgs > m_WorkManager.YieldQueueAfter;
                var bEmptyQ = !(q.Count() > 0);

                if (bMoreMessages || !bEmptyQ) // did we find messages in the queue
                {
                    m_WorkManager.m_TraceWriter.TraceMessage(string.Format("queue:{0} pushed back to queues, queue still have more work", qName));

                    leaveQ(qName, true); 
                }
                else
                {
                    var now = DateTime.UtcNow.Ticks;                    
                    // was queue previously empty? 
                    var bCheckedBefore = m_WorkManager.m_QueueManager.m_SuspectedEmptyQueues.TryGetValue(qName, out LastCheckedVal);

                    // Q was in suspected empty queue and has expired
                    if (bCheckedBefore && ((now - LastCheckedVal) > m_WorkManager.m_RemoveEmptyQueueAfterTicks))
                    {
                        m_WorkManager.m_TraceWriter.TraceMessage(string.Format("queue:{0} confirmed empty, and will be removed", qName));

                        // remove it from suspected queue 
                        m_WorkManager.m_QueueManager.m_SuspectedEmptyQueues.TryRemove(qName, out LastCheckedVal);

                        // remove from the queue list
                        await m_WorkManager.m_QueueManager.RemoveQueueAsync(qName);

                        // remove asscioated handler
                        m_WorkManager.m_DeferedTaskExec.AddWork(() => m_WorkManager.RemoveHandlerForQueue(qName));

                        // modify executers to reflect the current state
                        m_WorkManager.m_DeferedTaskExec.AddWork(m_WorkManager.TryDecreaseExecuters);                        
                    }
                    else
                    {
                        m_WorkManager.m_TraceWriter.TraceMessage(string.Format("queue:{0} pushed back to queues and flagged as an empty queue suspect ", qName));
                        // the queue was not a suspect before, or has not expired 
                        m_WorkManager.m_QueueManager.m_SuspectedEmptyQueues.AddOrUpdate(qName, now, (k, v) => { return v; });
                        leaveQ(qName, false);
                    }
                }
            }
        private async Task workLoopAsync()
        {
            var nLongDequeueWaitTimeMs = 20 * 1000;
            var nShortDequeueWaitTimeMs = 2 * 1000;
            var nNoQueueWaitTimeMS = 5  * 1000;
            var nPauseCheckMs    = 5  * 1000;

            while (m_KeepWorking)
            {
                    // pause check
                    while (m_Pause)
                        await Task.Delay(nPauseCheckMs);


                    // take the queue
                    var kvp = m_WorkManager.m_QueueManager.TakeQueueAsync();
                    
                    if (null == kvp.Value) // no queue to work on. 
                    {
                        // this will only happen if executers # are > than queues
                        // usually a situation that should resolve it self.
                        // well by the following logic 
                        m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Executer {0} found no q and will sleep for {1}", m_WorkerExecuterId, nNoQueueWaitTimeMS));

                        await FinilizeQueueWork(0, null, null); // check removal 
                        await Task.Delay(nNoQueueWaitTimeMS); // sleep as there is no point of retrying right away.

                        continue;
                    }

                    // got Q
                    var q               = kvp.Value;
                    var qName           = kvp.Key;
                    var nCurrentMessage = 0;

                    try
                    {

                        while (m_KeepWorking & !m_Pause)
                        {
                            nCurrentMessage++;

                            // processed the # of messages?
                            if (nCurrentMessage > m_WorkManager.YieldQueueAfter)
                                break; //-> to finally

                            // as long as we have other queues. we need to have a short wait time
                            var ActualTimeOut = m_WorkManager.m_QueueManager.Count > m_WorkManager.m_Executers.Count ?
                                nShortDequeueWaitTimeMs : nLongDequeueWaitTimeMs;
                             

                            using (var tx = m_WorkManager.StateManager.CreateTransaction())
                            {
                                var cResults = await q.TryDequeueAsync(tx,
                                                                       TimeSpan.FromMilliseconds(ActualTimeOut),
                                                                       CancellationToken.None);
                                if (cResults.HasValue)
                                {
                                    var handler = m_WorkManager.GetHandlerForQueue(qName);
                                    var wi = await handler.HandleWorkItem(cResults.Value);

                                    if (null != wi) // do we have an enqueue request? 
                                        await q.EnqueueAsync(tx, wi);

                                    await tx.CommitAsync();
                                    m_WorkManager.DecreaseBufferedWorkItems();
                                }
                                else
                                {
                                    break; // -> to finally
                                }
                            }
                        }
                    }
                    catch (TimeoutException to)
                    {
                        /* Queue is locked for enqueues */
                        m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Executer Dequeue Timeout after {0}: {1}", nLongDequeueWaitTimeMs, to.Message));
                        break; //-> to finally
                    }
                    catch (AggregateException aex)
                    {
                        var ae = aex.Flatten();
                        m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Executer encountered fatel error and will exit E:{0} StackTrace:{1}", ae.GetCombinedExceptionMessage(), ae.GetCombinedExceptionStackTrace()));

                        throw;
                    }
                    catch (Exception E)
                    {
                        m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Executer encountered fatel error and will exit E:{0} StackTrace:{1}", E.Message, E.StackTrace));

                        throw;
                    }
                    finally
                    {
                        await FinilizeQueueWork(nCurrentMessage, qName, q);
                    }
            }

                m_WorkManager.m_TraceWriter.TraceMessage(string.Format("Worker {0} exited loop", m_WorkerExecuterId));


            }
        }
    }
}
