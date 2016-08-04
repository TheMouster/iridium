﻿#region License
//=============================================================================
// Iridium - Porable .NET ORM 
//
// Copyright (c) 2015 Philippe Leybaert
//
// Permission is hereby granted, free of charge, to any person obtaining a copy 
// of this software and associated documentation files (the "Software"), to deal 
// in the Software without restriction, including without limitation the rights 
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the Software is 
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//=============================================================================
#endregion

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

#if IRIDIUM_SQLSERVER
namespace Iridium.DB.SqlServer
#elif IRIDIUM_MYSQL
namespace Iridium.DB.MySql
#else
namespace Iridium.DB.Sql
#endif
{
    public abstract class SqlAdoDataProvider<TConnection, TDialect> : SqlDataProvider<TDialect> where TConnection : DbConnection, new() where TDialect : SqlDialect, new()
    {
        public string ConnectionString { get; set; }

        private readonly ThreadLocal<DbConnection> _localConnection = new ThreadLocal<DbConnection>(true);
        private readonly ThreadLocal<Stack<DbTransaction>> _transactionStack = new ThreadLocal<Stack<DbTransaction>>(() => new Stack<DbTransaction>());

        protected SqlAdoDataProvider()
        {
        }

        protected SqlAdoDataProvider(string connectionString)
        {
            ConnectionString = connectionString;
        }

        private DbConnection Connection
        {
            get
            {
                var connection = _localConnection.Value;

                if (connection != null)
                {
                    if (connection.State == ConnectionState.Open)
                        return connection;

                    connection.Dispose();

                    ClearConnectionPool();
                }

                _localConnection.Value = CreateAndOpenConnection();

                return _localConnection.Value;
            }
        }

        protected virtual DbConnection CreateAndOpenConnection()
        {
            var connection = new TConnection() { ConnectionString = ConnectionString };

            connection.Open();

            return connection;
        }

        private void CloseConnection()
        {
            var connection = _localConnection.Value;

            if (connection != null && connection.State != ConnectionState.Closed)
                connection.Close();

            _localConnection.Value = null;
        }

        public abstract void ClearConnectionPool();

        protected DbCommand CreateCommand(string sqlQuery, Dictionary<string, object> parameters)
        {
            Debug.WriteLine(sqlQuery);

            DbCommand dbCommand = Connection.CreateCommand();

            dbCommand.CommandType = CommandType.Text;
            dbCommand.CommandText = sqlQuery;

            dbCommand.Transaction = CurrentTransaction;


            dbCommand.CommandText = Regex.Replace(sqlQuery, @"@(?<name>[a-z0-9A-Z_]+)", match => SqlDialect.CreateParameterExpression(match.Value.Substring(1)));

            if (parameters != null)
                foreach (var parameter in parameters)
                {
                    IDbDataParameter dataParameter = dbCommand.CreateParameter();

                    dataParameter.ParameterName = SqlDialect.CreateParameterExpression(parameter.Key);
                    dataParameter.Direction = ParameterDirection.Input;
                    dataParameter.Value = ConvertParameter(parameter.Value);

                    dbCommand.Parameters.Add(dataParameter);
                }

            return dbCommand;
        }

        protected virtual object ConvertParameter(object value)
        {
            if (value == null)
                return DBNull.Value;

            if (value is Enum)
                return Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), null);

            return value;
        }

        public override IEnumerable<Dictionary<string, object>> ExecuteSqlReader(string sql, QueryParameterCollection parameters)
        {
            try
            {
                BeginTransaction(IsolationLevel.None);

                List<Dictionary<string, object>> records = new List<Dictionary<string, object>>();

                using (var cmd = CreateCommand(sql, parameters?.AsDictionary()))
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            Dictionary<string, object> rec = new Dictionary<string, object>();

                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                string fieldName = reader.GetName(i);

                                if (reader.IsDBNull(i))
                                    rec[fieldName] = null;
                                else
                                    rec[fieldName] = reader.GetValue(i);
                            }

                            records.Add(rec);
                        }
                    }
                }

                return records;
            }
            finally
            {
                CommitTransaction();
            }
        }

        public override int ExecuteSql(string sql, QueryParameterCollection parameters)
        {
            try
            {
                BeginTransaction(IsolationLevel.None);

                using (var cmd = CreateCommand(sql, parameters?.AsDictionary()))
                {
                    return cmd.ExecuteNonQuery();
                }
            }
            finally
            {
                CommitTransaction();
            }
        }

        public override void Dispose()
        {
            foreach (var connection in _localConnection.Values)
            {
                connection.Dispose();
            }
        }

        public override void BeginTransaction(IsolationLevel isolationLevel)
        {
            System.Data.IsolationLevel adoIsolationLevel;

            switch (isolationLevel)
            {
                case IsolationLevel.None: _transactionStack.Value.Push(null);
                    return;
                    
                case IsolationLevel.Chaos: adoIsolationLevel = System.Data.IsolationLevel.Chaos;
                    break;
                case IsolationLevel.ReadUncommitted: adoIsolationLevel=System.Data.IsolationLevel.ReadUncommitted;
                    break;
                case IsolationLevel.ReadCommitted: adoIsolationLevel = System.Data.IsolationLevel.ReadCommitted;
                    break;
                case IsolationLevel.RepeatableRead: adoIsolationLevel = System.Data.IsolationLevel.RepeatableRead;
                    break;
                case IsolationLevel.Serializable: adoIsolationLevel = System.Data.IsolationLevel.Serializable;
                    break;
                case IsolationLevel.Snapshot: adoIsolationLevel = System.Data.IsolationLevel.Snapshot;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, null);
            }

            var transaction = Connection.BeginTransaction(adoIsolationLevel);

            _transactionStack.Value.Push(transaction);

            //_currentTransaction = transaction;
        }

        public override void CommitTransaction()
        {
            _transactionStack.Value.Pop()?.Commit();

            if (_transactionStack.Value.Count == 0)
                CloseConnection();
        }

        public override void RollbackTransaction()
        {
            _transactionStack.Value.Pop()?.Rollback();

            if (_transactionStack.Value.Count == 0)
                CloseConnection();
        }

        private DbTransaction CurrentTransaction
        {
            get { return _transactionStack.Value.Reverse().FirstOrDefault(t => t != null); }
        }

        //private DbTransaction _currentTransaction;

        //private readonly Stack<DbTransaction> _transactionStack = new Stack<DbTransaction>();
    }

    public interface ITransaction
    {
    }

    public class SqlAdoTransaction : ITransaction
    {
        private readonly DbTransaction _transaction;

        public SqlAdoTransaction()
        {
            _transaction = null;
        }

        public SqlAdoTransaction(DbTransaction transaction)
        {
            _transaction = transaction;
        }

        public void Commit()
        {
            _transaction?.Commit();
        }

        public void Rollback()
        {
            _transaction?.Rollback();
        }
    }
}