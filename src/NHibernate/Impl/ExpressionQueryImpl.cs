using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NHibernate.Engine;
using NHibernate.Engine.Query;
using NHibernate.Hql.Ast.ANTLR;
using NHibernate.Hql.Ast.ANTLR.Tree;
using NHibernate.Hql.Ast.ANTLR.Util;
using NHibernate.Type;
using NHibernate.Util;

namespace NHibernate.Impl
{
	internal class ExpressionQueryImpl : AbstractQueryImpl2
	{
		public ExpressionQueryImpl(IQueryExpression queryExpression, ISessionImplementor session, ParameterMetadata parameterMetadata)
			: base(queryExpression.Key, FlushMode.Unspecified, session, parameterMetadata)
		{
			QueryExpression = queryExpression;
		}

		protected ExpressionQueryImpl(
			IQueryExpression queryExpression, ISessionImplementor session, ParameterMetadata parameterMetadata, bool isFilter)
			: this(queryExpression, session, parameterMetadata)
		{
			_isFilter = isFilter;
		}

		public IQueryExpression QueryExpression { get; private set; }

		protected readonly bool _isFilter;

		/// <summary> 
		/// Warning: adds new parameters to the argument by side-effect, as well as mutating the query expression tree!
		/// </summary>
		protected override IQueryExpression ExpandParameters(IDictionary<string, TypedValue> namedParamsCopy)
		{
			if (namedParameterLists.Count == 0)
			{
				// Short circuit straight out
				return QueryExpression;
			}

			// Build a map from single parameters to lists
			var map = new Dictionary<string, IList<string>>();

			foreach (var me in namedParameterLists)
			{
				var name = me.Key;
				var vals = (IEnumerable) me.Value.Value;
				var type = me.Value.Type;

				var typedValues = (from object value in vals
								   select new TypedValue(type, value, false))
					.ToList();

				if (typedValues.Count == 1)
				{
					namedParamsCopy[name] = typedValues[0];
					continue;
				}

				var aliases = new string[typedValues.Count];
				var isJpaPositionalParam = parameterMetadata.GetNamedParameterDescriptor(name).JpaStyle;
				for (var index = 0; index < typedValues.Count; index++)
				{
					var value = typedValues[index];
					var alias = (isJpaPositionalParam ? 'x' + name : name + StringHelper.Underscore) + index + StringHelper.Underscore;
					namedParamsCopy[alias] = value;
					aliases[index] = alias;
				}

				map.Add(name, aliases);
			}

			if (map.Count == 0)
			{
				// No list parameter needs to be replaced: they are all single valued. They just need
				// to be retyped, which has been done above.
				return QueryExpression;
			}

			//TODO: Do we need to translate expression one more time here?
			// This is not much an issue anymore: ExpressionQueryImpl are currently created only with NhLinqExpression
			// which do cache their translation.
			var newTree = ParameterExpander.Expand(QueryExpression.Translate(Session.Factory, _isFilter), map);
			var key = new StringBuilder(QueryExpression.Key);

			foreach (var pair in map)
			{
				key.Append(' ');
				key.Append(pair.Key);
				key.Append(':');
				foreach (var s in pair.Value)
					key.Append(s);
			}

			return new ExpandedQueryExpression(QueryExpression, newTree, key.ToString());
		}
	}

	internal partial class ExpressionFilterImpl : ExpressionQueryImpl
	{
		private readonly object collection;

		public ExpressionFilterImpl(IQueryExpression queryExpression, object collection, ISessionImplementor session, ParameterMetadata parameterMetadata) 
			: base(queryExpression, session, parameterMetadata, true)
		{
			this.collection = collection;
		}

		public override IList List()
		{
			VerifyParameters();
			var namedParams = NamedParams;
			Before();
			try
			{
				return Session.ListFilter(collection, ExpandParameters(namedParams), GetQueryParameters(namedParams));
			}
			finally
			{
				After();
			}
		}

		public override IList<T> List<T>()
		{
			VerifyParameters();
			var namedParams = NamedParams;
			Before();
			try
			{
				//6.0 TODO: Add Session.ListFilter<T> that accepts IQueryExpression
				var result = Session.ListFilter(collection, ExpandParameters(namedParams), GetQueryParameters(namedParams));

				return result as IList<T> ?? result.Cast<T>().ToList();
			}
			finally
			{
				After();
			}
		}

		public override void List(IList results)
		{
			ArrayHelper.AddAll(results, List());
		}

		public override IEnumerable Enumerable()
		{
			throw new NotImplementedException();
		}

		public override IEnumerable<T> Enumerable<T>()
		{
			throw new NotImplementedException();
		}

		protected internal override IEnumerable<ITranslator> GetTranslators(ISessionImplementor session, QueryParameters queryParameters)
		{
			// NOTE: updates queryParameters.NamedParameters as (desired) side effect
			var queryExpression = ExpandParameters(queryParameters.NamedParameters);

			return CollectionFilterImpl.GetTranslators(session, queryParameters, queryExpression, collection);
		}

		public override IType[] TypeArray()
		{
			IList<IType> typeList = Types;
			int size = typeList.Count;
			var result = new IType[size + 1];
			for (int i = 0; i < size; i++)
			{
				result[i + 1] = typeList[i];
			}
			return result;
		}

		public override object[] ValueArray()
		{
			IList valueList = Values;
			int size = valueList.Count;
			var result = new object[size + 1];
			for (int i = 0; i < size; i++)
			{
				result[i + 1] = valueList[i];
			}
			return result;
		}
	}

	internal class ExpandedQueryExpression : IQueryExpression, ICacheableQueryExpression
	{
		private readonly IASTNode _tree;
		private ICacheableQueryExpression _cacheableExpression;

		public ExpandedQueryExpression(IQueryExpression queryExpression, IASTNode tree, string key)
		{
			_tree = tree;
			Key = key;
			Type = queryExpression.Type;
			ParameterDescriptors = queryExpression.ParameterDescriptors;
			 _cacheableExpression = queryExpression as ICacheableQueryExpression;
		}

		#region IQueryExpression Members

		public IASTNode Translate(ISessionFactoryImplementor sessionFactory, bool filter)
		{
			return _tree;
		}

		public string Key { get; private set; }

		public System.Type Type { get; private set; }

		public IList<NamedParameterDescriptor> ParameterDescriptors { get; private set; }

		#endregion

		public bool CanCachePlan => _cacheableExpression?.CanCachePlan ?? true;
	}

	internal class ParameterExpander
	{
		private readonly Dictionary<string, IList<string>> _map;
		private readonly IASTNode _tree;

		private ParameterExpander(IASTNode tree, Dictionary<string, IList<string>> map)
		{
			_tree = tree;
			_map = map;
		}

		public static IASTNode Expand(IASTNode tree, Dictionary<string, IList<string>> map)
		{
			var expander = new ParameterExpander(tree, map);

			return expander.Expand();
		}

		private IASTNode Expand()
		{
			var parameters = ParameterDetector.LocateParameters(_tree, new HashSet<string>(_map.Keys));
			var nodeMapping = new Dictionary<IASTNode, IEnumerable<IASTNode>>();

			foreach (IASTNode param in parameters)
			{
				var paramName = param.GetChild(0);
				var aliases = _map[paramName.Text];
				var astAliases = new List<IASTNode>();

				foreach (string alias in aliases)
				{
					IASTNode astAlias = param.DupNode();
					IASTNode astAliasName = paramName.DupNode();
					astAliasName.Text = alias;
					astAlias.AddChild(astAliasName);

					astAliases.Add(astAlias);
				}

				nodeMapping.Add(param, astAliases);
			}

			return DuplicateTree(_tree, nodeMapping);
		}

		private static IASTNode DuplicateTree(IASTNode ast, IDictionary<IASTNode, IEnumerable<IASTNode>> nodeMapping)
		{
			IASTNode thisNode = ast.DupNode();

			foreach (IASTNode child in ast)
			{
				IEnumerable<IASTNode> candidate;

				if (nodeMapping.TryGetValue(child, out candidate))
				{
					foreach (IASTNode replacement in candidate)
					{
						thisNode.AddChild(replacement);
					}
				}
				else
				{
					thisNode.AddChild(DuplicateTree(child, nodeMapping));
				}
			}

			return thisNode;
		}
	}

	internal class ParameterDetector : IVisitationStrategy
	{
		private readonly List<IASTNode> _nodes;
		private readonly HashSet<string> _parameterNames;
		private readonly IASTNode _tree;

		private ParameterDetector(IASTNode tree, HashSet<string> parameterNames)
		{
			_tree = tree;
			_parameterNames = parameterNames;
			_nodes = new List<IASTNode>();
		}

		#region IVisitationStrategy Members

		public void Visit(IASTNode node)
		{
			if ((node.Type == HqlSqlWalker.PARAM) || (node.Type == HqlSqlWalker.COLON))
			{
				string name = node.GetChild(0).Text;

				if (_parameterNames.Contains(name))
				{
					_nodes.Add(node);
				}
			}
		}

		#endregion

		public static IList<IASTNode> LocateParameters(IASTNode tree, HashSet<string> parameterNames)
		{
			var detector = new ParameterDetector(tree, parameterNames);

			return detector.LocateParameters();
		}

		private List<IASTNode> LocateParameters()
		{
			var nodeTraverser = new NodeTraverser(this);
			nodeTraverser.TraverseDepthFirst(_tree);

			return _nodes;
		}
	}
}
