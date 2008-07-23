#region License

/*
 * Copyright 2002-2008 the original author or authors.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *      http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#endregion

using System;
using Apache.NMS;
using Common.Logging;
using Spring.Context;
using Spring.Messaging.Nms.Connection;
using Spring.Messaging.Nms.Support;
using Spring.Messaging.Nms.Support.IDestinations;
using Spring.Objects.Factory;

namespace Spring.Messaging.Nms.Listener
{
    /// <summary>
    /// Common base class for all containers which need to implement listening
    /// based on a Connection (either shared or freshly obtained for each attempt).
    /// Inherits basic Connection and Session configuration handling from the
    /// <see cref="NmsAccessor"/> base class.
    /// </summary>
    /// <para>
    /// This class provides basic lifecycle management, in particular management
    /// of a shared Connection. Subclasses are supposed to plug into this
    /// lifecycle, implementing the <see cref="SharedConnectionEnabled"/> as well as
    ///
    /// </para>
    /// <remarks>
    ///
    /// </remarks>
    /// <author>Mark Pollack</author>
    public abstract class AbstractNmsListeningContainer : NmsDestinationAccessor, ILifecycle, IObjectNameAware, IDisposable
    {
        #region Logging

        private readonly ILog logger = LogManager.GetLogger(typeof(AbstractNmsListeningContainer));

        #endregion

        #region Fields

        private String clientId;

        protected bool autoStartup = true;
        
        private string objectName;
        
        private IConnection sharedConnection;

        private bool sharedConnectionStarted = false;
        
        protected object sharedConnectionMonitor = new object();
        
        private volatile bool active = false;
        
        private bool running = false;
        
        protected object lifecycleMonitor = new object();

        #endregion

        public string ClientId
        {
            set { clientId = value; }
            get { return clientId;  }
        }

        /// <summary> Set whether to automatically start the listener after initialization.
        /// <p>Default is "true"; set this to "false" to allow for manual startup.</p>
        /// </summary>
        public virtual bool AutoStartup
        {
            set { this.autoStartup = value; }
        }

        public string ObjectName
        {
            set { objectName = value; }
        }

        /// <summary>
        /// Gets a value indicating whether this container is currently running,
        /// that is, whether it has been started and not stopped yet.
        /// </summary>
        /// <value>
        /// 	<c>true</c> if this container is running; otherwise, <c>false</c>.
        /// </value>
        public bool IsRunning
        {
            get
            {
                lock (lifecycleMonitor)
                {
                    return (running && RunningAllowed);
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether this container's listeners are generally allowed to run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// >This implementation always returns <code>true</code>; the default 'running'
        /// state is purely determined by <see cref="Start"/>/<see cref="Stop"/>.
        /// </para>
        /// <para>
        /// Subclasses may override this method to check against temporary
        /// conditions that prevent listeners from actually running. In other words,
        /// they may apply further restrictions to the 'running' state, returning
        /// <code>false</code> if such a restriction prevents listeners from running.
        /// </para>
        /// </remarks>
        /// <value><c>true</c> if running allowed; otherwise, <c>false</c>.</value>
        protected virtual bool RunningAllowed
        {
            get {
                return true; }
        }

        public virtual bool Active
        {
            get
            {
                lock (this.lifecycleMonitor)
                {
                    return this.active;
                }
            }

        }

        /// <summary> Return whether a shared NMS IConnection should be maintained
        /// by this listener container base class.
        /// </summary>
        /// <seealso cref="AbstractMessageListenerContainer.SharedConnection">
        /// </seealso>
        protected abstract bool SharedConnectionEnabled { get; }

        /// <summary>
        /// Gets the shared connection maintained by this container.
        /// Available after initialization.
        /// </summary>
        /// <value>The shared connection (never null)</value>
        /// <exception cref="InvalidOperationException">if this container does not maintain a
        /// shared Connection, or if the Connection hasn't been initialized yet.
        /// </exception>
        /// <see cref="SharedConnectionEnabled"/>
        protected IConnection SharedConnection
        {
            get
            {
                if (!SharedConnectionEnabled)
                {
                    throw new InvalidOperationException("This listener container does not maintain a shared IConnection");
                }
                lock (this.sharedConnectionMonitor)
                {
                    if (this.sharedConnection == null)
                    {
                        throw new SharedConnectionNotInitializedException("This listener container's shared Connection has not been initialized yet");
                    }
                    return this.sharedConnection;
                }
            }
        }

        public override void AfterPropertiesSet()
        {
            base.AfterPropertiesSet();
            ValidateConfiguration();
            Initialize();
        }

        /// <summary>
        /// Validates the configuration of this container.  The default implementation
        /// is empty.  To be overriden in subclasses.
        /// </summary>
        protected virtual void ValidateConfiguration()
        {
            
        }

        public void Dispose()
        {
            Shutdown();
        }


        /// <summary>
        /// Initializes this container.  Creates a Connection, starts the Connection
        /// (if the property <see cref="AutoStartup"/> hasn't been turned off), and calls
        /// <see cref="DoInitialize"/>.
        /// </summary>
        /// <exception cref="NMSException">If startup failed</exception>
        public virtual void Initialize()
        {
            try
            {
                lock (this.lifecycleMonitor)
                {
                    this.active = true;
                    System.Threading.Monitor.PulseAll(this.lifecycleMonitor);
                }

                if (this.autoStartup)
                {
                    DoStart();
                }

                DoInitialize();             

            }
            catch (Exception)
            {
                lock (this.sharedConnectionMonitor)
                {
                    ConnectionFactoryUtils.ReleaseConnection(sharedConnection, ConnectionFactory, autoStartup);
                }
                throw;
            }
        }

        public virtual void Shutdown()
        {
            logger.Debug("Shutting down message listener container");
            bool wasRunning = false;
            lock (this.lifecycleMonitor)
            {
                wasRunning = this.running;
                this.running = false;
                this.active = false;
                System.Threading.Monitor.PulseAll(this.lifecycleMonitor);
            }

            if (wasRunning && SharedConnectionEnabled)
            {
                try
                {
                    StopSharedConnection();
                } catch (Exception ex)
                {
                    logger.Debug("Could not stop NMS Connection on shutdown", ex);
                }
            }

            // Shut down the invokers
            try
            {
                DoShutdown();
            }
            finally
            {
                lock (this.sharedConnectionMonitor)
                {
                    ConnectionFactoryUtils.ReleaseConnection(this.sharedConnection, ConnectionFactory, false);
                }
            }
        }

        /// <summary>
        /// Starts this container.
        /// </summary>
        /// <exception cref="NMSException">if starting failed.</exception>
        public void Start()
        {
            DoStart();
        }

        protected virtual void DoStart()
        {
            // Lazily establish a shared Connection, if necessary.
            if (SharedConnectionEnabled)
            {
                EstablishSharedConnection();
            }

            lock (this.lifecycleMonitor)
            {
                running = true;
                System.Threading.Monitor.PulseAll(this.lifecycleMonitor);
            }

            // Start the shared Connection, if any.
            if (SharedConnectionEnabled)
            {
                StartSharedConnection();
            }
        }

        /// <summary>
        /// Stops this container.
        /// </summary>
        /// <exception cref="NMSException">if stopping failed.</exception>
        public void Stop()
        {
            DoStop();
        }

        /// <summary>
        /// Notify all invoker tasks and stop the shared Connection, if any.
        /// </summary>
        /// <exception cref="NMSException">if thrown by NMS API methods.</exception>
        /// <see cref="StopSharedConnection"/>
        protected virtual void DoStop()
        {
            lock (this.lifecycleMonitor)
            {
                this.running = false;
                System.Threading.Monitor.PulseAll(this.lifecycleMonitor);
            }

            if (SharedConnectionEnabled)
            {
                StopSharedConnection();
            }
        }

        /// <summary>
        /// Register any invokers within this container.
        /// Subclasses need to implement this method for their specific
        /// invoker management process.  A shared Connection, if any, will already have been
        /// started at this point.
        /// </summary>
        protected abstract void DoInitialize();


        /// <summary>
        /// Close the registered invokers.  Subclasses need to implement this method
        /// for their specific invoker management process. A shared Connection, if any,
        /// will automatically be closed afterwards.
        /// </summary>
        protected abstract void DoShutdown();


        /// <summary>
        /// Establishes a shared Connection for this container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The default implementation delegates to <see cref="CreateSharedConnection"/>
        /// which does one immediate attempt and throws an exception if it fails.
        /// Can be overridden to have a recovery process in place, retrying
        /// until a Connection can be successfully established.
        /// </para>
        /// </remarks>
        /// <exception cref="NMSException">If thrown by NMS API methods</exception>
        protected virtual void EstablishSharedConnection()
        {
            lock (sharedConnectionMonitor)
            {
                if (sharedConnection == null)
                {
                    sharedConnection = CreateSharedConnection();
                    logger.Debug("Established shared NMS Connection");
                }
            }
        }

        /// <summary>
        /// Refreshes the shared connection that this container holds.
        /// </summary>
        /// <remarks>
        /// Called on startup and also after an infrastructure exception
        /// that occurred during invoker setup and/or execution.
        /// </remarks>
        /// <exception cref="NMSException">If thrown by NMS API methods</exception>
        protected void RefreshSharedConnection()
        {
            lock (sharedConnectionMonitor)
            {
                ConnectionFactoryUtils.ReleaseConnection(sharedConnection, ConnectionFactory, sharedConnectionStarted);
                sharedConnection = CreateSharedConnection();
                if (sharedConnectionStarted)
                {
                    sharedConnection.Start();
                }
            }
            /*
            bool running = IsRunning;
            lock (this.sharedConnectionMonitor)
            {
                NmsUtils.CloseConnection(this.sharedConnection, running);

                IConnection con = CreateConnection();
                try
                {
                    PrepareSharedConnection(con);
                }
                catch (Exception)
                {
                    NmsUtils.CloseConnection(con);
                    throw;
                }
                this.sharedConnection = con;
            }*/
        }

        /// <summary>
        /// Creates the shared connection for this container.
        /// </summary>
        /// <remarks>
        /// The default implementation creates a standard Connection
        /// and prepares it through <see cref="PrepareSharedConnection"/>
        /// </remarks>
        /// <returns>the prepared Connection</returns>
        /// <exception cref="NMSException">if the creation failed.</exception>
        protected virtual IConnection CreateSharedConnection()
        {
            IConnection con = CreateConnection();
            try
            {
                PrepareSharedConnection(con);
                return con;
            } catch (NMSException ex)
            {
                NmsUtils.CloseConnection(con);
                throw;
            }
        }

        /// <summary>
        /// Prepares the given connection, which is about to be registered
        /// as shared Connection for this container.
        /// </summary>
        /// <remarks>
        /// The default implementation sets the specified client id, if any.
        /// Subclasses can override this to apply further settings.
        /// </remarks>
        /// <param name="connection">The connection to prepare.</param>
        /// <exception cref="NMSException">If the preparation efforts failed.</exception>
        protected virtual void PrepareSharedConnection(IConnection connection)
        {
            if (ClientId != null)
            {
                connection.ClientId = ClientId;
            }
        }


        /// <summary>
        /// Starts the shared connection.
        /// </summary>
        /// <exception cref="NMSException">If thrown by NMS API methods</exception>
        /// <see cref="Start"/>
        protected virtual void StartSharedConnection()
        {
            lock (sharedConnectionMonitor)
            {
                if (sharedConnection != null)
                {
                    try
                    {
                        sharedConnectionStarted = true;
                        sharedConnection.Start();
                    }
                    catch (Exception ex)
                    {
                        logger.Warn("Ignoring Connection start exception - assuming already started", ex);
                    }
                }
            }
        }

        protected virtual void StopSharedConnection()
        {
            lock (this.sharedConnectionMonitor)
            {
                if (this.sharedConnection != null)
                {
                    try
                    {
                        this.sharedConnectionStarted = false;
                        this.sharedConnection.Stop();
                    }
                    catch (System.InvalidOperationException ex)
                    {
                        logger.Warn("Ignoring Connection stop exception - assuming already stopped", ex);
                    }
                }
            }
        }

    }

    /// <summary>
    /// Exception that indicates that the initial setup of this container's
    /// shared Connection failed. This is indicating to invokers that they need
    /// to establish the shared Connection themselves on first access.
    /// </summary>
    public class SharedConnectionNotInitializedException : ApplicationException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SharedConnectionNotInitializedException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public SharedConnectionNotInitializedException(string message) : base(message)
        {
        }
    }
}