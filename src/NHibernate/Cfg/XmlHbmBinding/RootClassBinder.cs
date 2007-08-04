using System.Xml;

using NHibernate.Mapping;
using NHibernate.Type;
using NHibernate.Util;

namespace NHibernate.Cfg.XmlHbmBinding
{
	public class RootClassBinder : ClassBinder
	{
		public RootClassBinder(Binder parent)
			: base(parent)
		{
		}

		public RootClassBinder(Mappings mappings, XmlNamespaceManager namespaceManager)
			: base(mappings, namespaceManager)
		{
		}

		public override void Bind(XmlNode node)
		{
			RootClass rootClass = new RootClass();
			BindClass(node, rootClass);

			//TABLENAME
			string schema = GetAttributeValue(node, "schema") ?? mappings.SchemaName;
			string tableName = GetClassTableName(rootClass, node);

			Table table = mappings.AddTable(schema, tableName);
			((ITableOwner) rootClass).Table = table;

			LogInfo("Mapping class: {0} -> {1}", rootClass.Name, rootClass.Table.Name);

			//MUTABLE
			rootClass.IsMutable = "true".Equals(GetAttributeValue(node, "mutable") ?? "true");

			//WHERE
			rootClass.Where = GetAttributeValue(node, "where") ?? rootClass.Where;

			//CHECK
			string check = GetAttributeValue(node, "check");
			if (check != null)
				table.AddCheckConstraint(check);

			//POLYMORPHISM
			rootClass.IsExplicitPolymorphism = "explicit".Equals(GetAttributeValue(node, "polymorphism"));

			BindChildNodes(node, rootClass, table);
			rootClass.CreatePrimaryKey(dialect);
			PropertiesFromXML(node, rootClass);
			mappings.AddClass(rootClass);
		}

		private void BindChildNodes(XmlNode node, RootClass rootClass, Table table)
		{
			foreach (XmlNode subnode in node.ChildNodes)
			{
				string name = subnode.LocalName; //Name;
				string propertyName = GetPropertyName(subnode);

				//I am only concerned with elements that are from the nhibernate namespace
				if (subnode.NamespaceURI != Configuration.MappingSchemaXMLNS)
					continue;

				switch (name)
				{
					case "id":
						SimpleValue id = new SimpleValue(table);
						rootClass.Identifier = id;

						if (propertyName == null)
						{
							BindSimpleValue(subnode, id, false, RootClass.DefaultIdentifierColumnName, mappings);
							if (id.Type == null)
								throw new MappingException("must specify an identifier type: " + rootClass.MappedClass.Name);
							//model.IdentifierProperty = null;
						}
						else
						{
							BindSimpleValue(subnode, id, false, propertyName, mappings);
							id.SetTypeByReflection(rootClass.MappedClass, propertyName, PropertyAccess(subnode, mappings));
							Mapping.Property prop = new Mapping.Property(id);
							BindProperty(subnode, prop, mappings);
							rootClass.IdentifierProperty = prop;
						}

						if (id.Type.ReturnedClass.IsArray)
							throw new MappingException("illegal use of an array as an identifier (arrays don't reimplement equals)");

						MakeIdentifier(subnode, id, mappings);
						break;

					case "composite-id":
						Component compId = new Component(rootClass);
						rootClass.Identifier = compId;
						if (propertyName == null)
						{
							BindComponent(subnode, compId, null, rootClass.Name, "id", false, mappings);
							rootClass.HasEmbeddedIdentifier = compId.IsEmbedded;
							//model.IdentifierProperty = null;
						}
						else
						{
							System.Type reflectedClass = GetPropertyType(subnode, mappings, rootClass.MappedClass, propertyName);
							BindComponent(subnode, compId, reflectedClass, rootClass.Name, propertyName, false, mappings);
							Mapping.Property prop = new Mapping.Property(compId);
							BindProperty(subnode, prop, mappings);
							rootClass.IdentifierProperty = prop;
						}
						MakeIdentifier(subnode, compId, mappings);

						System.Type compIdClass = compId.ComponentClass;
						if (!ReflectHelper.OverridesEquals(compIdClass))
							throw new MappingException(
								"composite-id class must override Equals(): " + compIdClass.FullName
								);

						if (!ReflectHelper.OverridesGetHashCode(compIdClass))
							throw new MappingException(
								"composite-id class must override GetHashCode(): " + compIdClass.FullName
								);

						// Serializability check not ported
						break;

					case "version":
					case "timestamp":
						//VERSION / TIMESTAMP
						BindVersioningProperty(table, subnode, name, rootClass);
						break;

					case "discriminator":
						//DISCRIMINATOR
						SimpleValue discrim = new SimpleValue(table);
						rootClass.Discriminator = discrim;
						BindSimpleValue(subnode, discrim, false, RootClass.DefaultDiscriminatorColumnName, mappings);
						if (discrim.Type == null)
						{
							discrim.Type = NHibernateUtil.String;
							foreach (Column col in discrim.ColumnCollection)
							{
								col.Type = NHibernateUtil.String;
								break;
							}
						}
						rootClass.IsPolymorphic = true;
						if (subnode.Attributes["force"] != null && "true".Equals(subnode.Attributes["force"].Value))
							rootClass.IsForceDiscriminator = true;
						if (subnode.Attributes["insert"] != null && "false".Equals(subnode.Attributes["insert"].Value))
							rootClass.IsDiscriminatorInsertable = false;
						break;

					case "jcs-cache":
					case "cache":
						XmlAttribute usageNode = subnode.Attributes["usage"];
						rootClass.CacheConcurrencyStrategy = (usageNode != null) ? usageNode.Value : null;
						XmlAttribute regionNode = subnode.Attributes["region"];
						rootClass.CacheRegionName = (regionNode != null) ? regionNode.Value : null;

						break;
				}
			}
		}

		private void BindVersioningProperty(Table table, XmlNode node, string name, PersistentClass entity)
		{
			string propertyName = GetAttributeValue(node, "name");
			SimpleValue simpleValue = new SimpleValue(table);
			BindSimpleValue(node, simpleValue, false, propertyName);

			if (simpleValue.Type == null)
				simpleValue.Type = simpleValue.Type ?? GetVersioningPropertyType(name);

			Mapping.Property property = new Mapping.Property(simpleValue);
			BindProperty(node, property, mappings);

			// for version properties marked as being generated, make sure they are "always"
			// generated; "insert" is invalid. This is dis-allowed by the schema, but just to make
			// sure...

			if (property.Generation == PropertyGeneration.Insert)
				throw new MappingException("'generated' attribute cannot be 'insert' for versioning property");

			MakeVersion(node, simpleValue);
			entity.Version = property;
			entity.AddProperty(property);
		}

		private static NullableType GetVersioningPropertyType(string name)
		{
			return "version".Equals(name) ? NHibernateUtil.Int32 : NHibernateUtil.Timestamp;
		}

		public static void MakeVersion(XmlNode node, SimpleValue model)
		{
			// VERSION UNSAVED-VALUE
			model.NullValue = GetAttributeValue(node, "unsaved-value");
		}
	}
}