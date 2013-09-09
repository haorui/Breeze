﻿using NHibernate;
using NHibernate.Cfg;
using NHibernate.Engine;
using NHibernate.Id;
using NHibernate.Mapping;
using NHibernate.Metadata;
using NHibernate.Persister.Entity;
using NHibernate.Type;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Breeze.WebApi.NH
{
    /// <summary>
    /// Builds a data structure containing the metadata required by Breeze.
    /// <see cref="http://www.breezejs.com/documentation/breeze-metadata-format"/>
    /// </summary>
    public class NHBreezeMetadata
    {
        private ISessionFactory _sessionFactory;
        private Configuration _configuration;
        private Dictionary<string, object> _map;
        private List<Dictionary<string, object>> _typeList;
        private Dictionary<string, object> _resourceMap;
        private HashSet<string> _typeNames;
        private Dictionary<string, string> _fkMap;

        public static readonly string FK_MAP = "fkMap";

        public NHBreezeMetadata(ISessionFactory sessionFactory, Configuration configuration)
        {
            _sessionFactory = sessionFactory;
            _configuration = configuration;
        }

        /// <summary>
        /// Build the Breeze metadata as a nested Dictionary.  
        /// The result can be converted to JSON and sent to the Breeze client.
        /// </summary>
        /// <returns></returns>
        public IDictionary<string, object> BuildMetadata()
        {
            InitMap();

            IDictionary<string, IClassMetadata> classMeta = _sessionFactory.GetAllClassMetadata();
            //IDictionary<string, ICollectionMetadata> collectionMeta = _sessionFactory.GetAllCollectionMetadata();

            foreach (var meta in classMeta.Values)
            {
                AddClass(meta);
            }
            return _map;
        }

        /// <summary>
        /// Populate the metadata header.
        /// </summary>
        void InitMap()
        {
            _map = new Dictionary<string, object>();
            _typeList = new List<Dictionary<string, object>>();
            _typeNames = new HashSet<string>();
            _resourceMap = new Dictionary<string, object>();
            _fkMap = new Dictionary<string, string>();
            _map.Add("localQueryComparisonOptions", "caseInsensitiveSQL");
            _map.Add("structuralTypes", _typeList);
            _map.Add("resourceEntityTypeMap",_resourceMap);
            _map.Add(FK_MAP, _fkMap);
        }

        /// <summary>
        /// Add the metadata for an entity.
        /// </summary>
        /// <param name="meta"></param>
        void AddClass(IClassMetadata meta)
        {
            var type = meta.GetMappedClass(EntityMode.Poco);

            // "Customer:#Breeze.Nhibernate.NorthwindIBModel": {
            var classKey = type.Name + ":#" + type.Namespace;
            var cmap = new Dictionary<string, object>();
            _typeList.Add(cmap);

            cmap.Add("shortName", type.Name);
            cmap.Add("namespace", type.Namespace);

            var persistentClass = _configuration.GetClassMapping(type);
            var superClass = persistentClass.Superclass;
            if (superClass != null) 
            {
                var superType = superClass.MappedClass;
                var baseTypeName = superType.Name + ":#" + superType.Namespace;
                cmap.Add("baseTypeName", baseTypeName);
            }

            var entityPersister = meta as IEntityPersister;
            var generator = entityPersister != null ? entityPersister.IdentifierGenerator : null;
            if (generator != null)
            {
                string genType = null;
                if (generator is IdentityGenerator) genType = "Identity";
                else if (generator is Assigned) genType = "None";
                else genType = "KeyGenerator";
                cmap.Add("autoGeneratedKeyType", genType); // TODO find the real generator
            }

            var resourceName = Pluralize(type.Name); // TODO find the real name
            cmap.Add("defaultResourceName", resourceName);
            _resourceMap.Add(resourceName, classKey);

            var dataList = new List<Dictionary<string, object>>();
            cmap.Add("dataProperties", dataList);
            var navList = new List<Dictionary<string, object>>();
            cmap.Add("navigationProperties", navList);

            AddClassProperties(meta, persistentClass, dataList, navList);
        }

        /// <summary>
        /// Add the properties for an entity.
        /// </summary>
        /// <param name="meta"></param>
        /// <param name="pClass"></param>
        /// <param name="dataList">will be populated with the data properties of the entity</param>
        /// <param name="navList">will be populated with the navigation properties of the entity</param>
        void AddClassProperties(IClassMetadata meta, PersistentClass pClass, List<Dictionary<string, object>> dataList, List<Dictionary<string, object>> navList)
        {
            // maps column names to their related data properties.  Used in MakeAssociationProperty to convert FK column names to entity property names.
            var relatedDataPropertyMap = new Dictionary<string, Dictionary<string, object>>();

            var persister = meta as AbstractEntityPersister;
            var type = pClass.MappedClass;

            var propNames = meta.PropertyNames;
            var propTypes = meta.PropertyTypes;
            var propNull = meta.PropertyNullability;
            for (int i = 0; i < propNames.Length; i++)
            {
                var propName = propNames[i];
                if (!hasOwnProperty(pClass, propName)) continue;  // skip property defined on superclass

                var propType = propTypes[i];
                if (!propType.IsAssociationType)    // skip association types until we handle all the data types, so the relatedDataPropertyMap will be populated.
                {
                    var propColumns = pClass.GetProperty(propName).ColumnIterator.ToList();
                    if (propType.IsComponentType)
                    {
                        // complex type
                        var compType = (ComponentType)propType;
                        var complexTypeName = AddComponent(compType, propColumns);
                        var compMap = new Dictionary<string, object>();
                        compMap.Add("nameOnServer", propName);
                        compMap.Add("complexTypeName", complexTypeName);
                        compMap.Add("isNullable", propNull[i]);
                        dataList.Add(compMap);
                    }
                    else
                    {
                        // data property
                        var col = propColumns.Count() == 1 ? propColumns[0] as Column : null;
                        var isKey = meta.HasNaturalIdentifier && meta.NaturalIdentifierProperties.Contains(i);
                        var isVersion = meta.IsVersioned && i == meta.VersionProperty;

                        var dmap = MakeDataProperty(propName, propType.Name, propNull[i], col, isKey, isVersion);
                        dataList.Add(dmap);

                        var columnNameString = GetPropertyColumnNames(persister, propName); 
                        relatedDataPropertyMap.Add(columnNameString, dmap);
                    }
                }
            }


            // Hibernate identifiers are excluded from the list of data properties, so we have to add them separately
            if (meta.HasIdentifierProperty && hasOwnProperty(pClass, meta.IdentifierPropertyName))
            {
                var dmap = MakeDataProperty(meta.IdentifierPropertyName, meta.IdentifierType.Name, false, null, true, false);
                dataList.Insert(0, dmap);

                var columnNameString = GetPropertyColumnNames(persister, meta.IdentifierPropertyName);
                relatedDataPropertyMap.Add(columnNameString, dmap);
            }
            else if (meta.IdentifierType != null && meta.IdentifierType.IsComponentType 
                && pClass.Identifier is Component && ((Component)pClass.Identifier).Owner == pClass)
            {
                // composite key is a ComponentType
                var compType = (ComponentType)meta.IdentifierType;
                var compNames = compType.PropertyNames;
                for (int i = 0; i < compNames.Length; i++)
                {
                    var compName = compNames[i];

                    var propType = compType.Subtypes[i];
                    if (!propType.IsAssociationType)
                    {
                        var dmap = MakeDataProperty(compName, propType.Name, compType.PropertyNullability[i], null, true, false);
                        dataList.Insert(0, dmap);
                    }
                    else
                    {
                        var manyToOne = propType as ManyToOneType;
                        //var joinable = manyToOne.GetAssociatedJoinable(this._sessionFactory);
                        var propColumnNames = GetPropertyColumnNames(persister, compName);

                        var assProp = MakeAssociationProperty(type, (IAssociationType)propType, compName, propColumnNames, pClass, relatedDataPropertyMap, true);
                        navList.Add(assProp);
                    }
                }
            }

            // We do the association properties after the data properties, so we can do the foreign key lookups
            for (int i = 0; i < propNames.Length; i++)
            {
                var propName = propNames[i];
                if (!hasOwnProperty(pClass, propName)) continue;  // skip property defined on superclass 

                var propType = propTypes[i];
                if (propType.IsAssociationType)
                {
                    // navigation property
                    var propColumnNames = GetPropertyColumnNames(persister, propName);
                    var assProp = MakeAssociationProperty(type, (IAssociationType)propType, propName, propColumnNames, pClass, relatedDataPropertyMap, false);
                    navList.Add(assProp);
                }
            }
        }

        bool hasOwnProperty(PersistentClass pClass, string propName) 
        {
            return pClass.GetProperty(propName).PersistentClass == pClass;
        }

        /// <summary>
        /// Adds a complex type definition
        /// </summary>
        /// <param name="compType">The complex type</param>
        /// <param name="propColumns">The columns which the complex type spans.  These are used to get the length and defaultValues</param>
        /// <returns>The class name and namespace in the form "Location:#Breeze.Nhibernate.NorthwindIBModel"</returns>
        string AddComponent(ComponentType compType, List<ISelectable> propColumns)
        {
            var type = compType.ReturnedClass;

            // "Location:#Breeze.Nhibernate.NorthwindIBModel"
            var classKey = type.Name + ":#" + type.Namespace;
            if (_typeNames.Contains(classKey))
            {
                // Only add a complex type definition once.
                return classKey;
            }

            var cmap = new Dictionary<string, object>();
            _typeList.Insert(0, cmap);
            _typeNames.Add(classKey);

            cmap.Add("shortName", type.Name);
            cmap.Add("namespace", type.Namespace);
            cmap.Add("isComplexType", true);

            var dataList = new List<Dictionary<string, object>>();
            cmap.Add("dataProperties", dataList);

            var propNames = compType.PropertyNames;
            var propTypes = compType.Subtypes;
            var propNull = compType.PropertyNullability;

            var colIndex = 0;
            for (int i = 0; i < propNames.Length; i++)
            {
                var propType = propTypes[i];
                var propName = propNames[i];
                if (propType.IsComponentType)
                {
                    // nested complex type
                    var compType2 = (ComponentType)propType;
                    var span = compType2.GetColumnSpan((IMapping) _sessionFactory);
                    var subColumns = propColumns.Skip(colIndex).Take(span).ToList();
                    var complexTypeName = AddComponent(compType2, subColumns);
                    var compMap = new Dictionary<string, object>();
                    compMap.Add("nameOnServer", propName);
                    compMap.Add("complexTypeName", complexTypeName);
                    compMap.Add("isNullable", propNull[i]);
                    dataList.Add(compMap);
                    colIndex += span;
                }
                else
                {
                    // data property
                    var col = propColumns[colIndex] as Column;
                    var dmap = MakeDataProperty(propName, propType.Name, propNull[i], col, false, false);
                    dataList.Add(dmap);
                    colIndex++;
                }
            }
            return classKey;
        }

        /// <summary>
        /// Make data property metadata for the entity
        /// </summary>
        /// <param name="propName">name of the property on the server</param>
        /// <param name="typeName">data type of the property, e.g. Int32</param>
        /// <param name="isNullable">whether the property is nullable in the database</param>
        /// <param name="col">Column object, used for maxLength and defaultValue</param>
        /// <param name="isKey">true if this property is part of the key for the entity</param>
        /// <param name="isVersion">true if this property contains the version of the entity (for a concurrency strategy)</param>
        /// <returns></returns>
        private Dictionary<string, object> MakeDataProperty(string propName, string typeName, bool isNullable, Column col, bool isKey, bool isVersion)
        {
            string newType;
            typeName = (BreezeTypeMap.TryGetValue(typeName, out newType)) ? newType : typeName;

            var dmap = new Dictionary<string, object>();
            dmap.Add("nameOnServer", propName);
            dmap.Add("dataType", typeName);
            dmap.Add("isNullable", isNullable);

            if (col != null && col.DefaultValue != null)
            {
                dmap.Add("defaultValue", col.DefaultValue);
            }
            if (isKey)
            {
                dmap.Add("isPartOfKey", true);
            }
            if (isVersion)
            {
                dmap.Add("concurrencyMode", "Fixed");
            }

            var validators = new List<Dictionary<string, string>>();

            if (!isNullable)
            {
                validators.Add(new Dictionary<string, string>() {
                    {"name", "required" },
                });
            }
            if (col != null && col.IsLengthDefined())
            {
                dmap.Add("maxLength", col.Length);

                validators.Add(new Dictionary<string, string>() {
                    {"maxLength", col.Length.ToString() },
                    {"name", "maxLength" }
                });
            }

            string validationType;
            if (ValidationTypeMap.TryGetValue(typeName, out validationType))
            {
                validators.Add(new Dictionary<string, string>() {
                    {"name", validationType },
                });
            }

            if (validators.Any())
                dmap.Add("validators", validators);

            return dmap;
        }


        /// <summary>
        /// Make association property metadata for the entity.
        /// Also populates the _fkMap which is used for related-entity fixup in NHContext.FixupRelationships
        /// </summary>
        /// <param name="propType"></param>
        /// <param name="propName"></param>
        /// <param name="pClass"></param>
        /// <param name="relatedDataPropertyMap"></param>
        /// <returns></returns>
        private Dictionary<string, object> MakeAssociationProperty(Type containingType, IAssociationType propType, string propName, string columnNames, PersistentClass pClass, Dictionary<string, Dictionary<string, object>> relatedDataPropertyMap, bool isKey)
        {
            var nmap = new Dictionary<string, object>();
            nmap.Add("nameOnServer", propName);

            var relatedEntityType = GetEntityType(propType.ReturnedClass, propType.IsCollectionType);
            nmap.Add("entityTypeName", relatedEntityType.Name + ":#" + relatedEntityType.Namespace);
            nmap.Add("isScalar", !propType.IsCollectionType);

            // the associationName must be the same at both ends of the association.
            nmap.Add("associationName", GetAssociationName(containingType.Name, relatedEntityType.Name, (propType is OneToOneType)));

            // The foreign key columns usually applies for many-to-one and one-to-one associations
            if (!propType.IsCollectionType)
            {
                var entityRelationship = pClass.EntityName + '.' + propName;
                Dictionary<string, object> relatedDataProperty;
                if (relatedDataPropertyMap.TryGetValue(columnNames, out relatedDataProperty))
                {
                    var fkName = (string) relatedDataProperty["nameOnServer"];
                    nmap.Add("foreignKeyNamesOnServer", new string[] { fkName });
                    _fkMap.Add(entityRelationship, fkName);
                    if (isKey)
                    {
                        if (!relatedDataProperty.ContainsKey("isPartOfKey"))
                        {
                            relatedDataProperty.Add("isPartOfKey", true);
                        }
                    }
                }
                else
                {
                    nmap.Add("foreignKeyNamesOnServer", columnNames);
                    nmap.Add("ERROR", "Could not find matching fk for property " + entityRelationship);
                    _fkMap.Add(entityRelationship, columnNames);
                    throw new ArgumentException("Could not find matching fk for property " + entityRelationship);
                }
            }
            return nmap;
        }

        /// <summary>
        /// Get the column names for a given property as a comma-delimited string of unbracketed names.
        /// </summary>
        /// <param name="persister"></param>
        /// <param name="propertyName"></param>
        /// <returns></returns>
        string GetPropertyColumnNames(AbstractEntityPersister persister, string propertyName)
        {
            var propColumnNames = persister.GetPropertyColumnNames(propertyName);
            if (propColumnNames.Length == 0)
            {
                // this happens when the property is part of the key
                propColumnNames = persister.KeyColumnNames;
            }
            var sb = new StringBuilder();
            foreach (var s in propColumnNames)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(UnBracket(s));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Get the column name without square brackets around it.  E.g. "[OrderID]" -> "OrderID" 
        /// Because sometimes Hibernate gives us brackets, and sometimes it doesn't.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        string UnBracket(string name)
        {
            return (name[0] == '[') ? name.Substring(1, name.Length - 2) : name;
        }

        /// <summary>
        /// Get the Breeze name of the entity type.
        /// For collections, Breeze expects the name of the element type.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="isCollectionType"></param>
        /// <returns></returns>
        Type GetEntityType(Type type, bool isCollectionType)
        {
            if (!isCollectionType)
            {
                return type;
            }
            else if (type.HasElementType)
            {
                return type.GetElementType();
            }
            else if (type.IsGenericType)
            {
                return type.GetGenericArguments()[0];
            }
            throw new ArgumentException("Don't know how to handle " + type);
        }

        /// <summary>
        /// lame pluralizer.  Assumes we just need to add a suffix.
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        string Pluralize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var last = s.Length - 1;
            var c = s[last];
            switch (c)
            {
                case 'y':
                    return s.Substring(0, last) + "ies";
                default:
                    return s + 's';
            }
        }

        /// <summary>
        /// Creates an association name from two entity names.
        /// For consistency, puts the entity names in alphabetical order.
        /// </summary>
        /// <param name="name1"></param>
        /// <param name="name2"></param>
        /// <param name="isOneToOne">if true, adds the one-to-one suffix</param>
        /// <returns></returns>
        string GetAssociationName(string name1, string name2, bool isOneToOne)
        {
            if (name1.CompareTo(name2) < 0)
                return ASSN + name1 + '_' + name2 + (isOneToOne ? ONE2ONE : null);
            else
                return ASSN + name2 + '_' + name1 + (isOneToOne ? ONE2ONE : null);
        }
        const string ONE2ONE = "_1to1";
        const string ASSN = "AN_";

        // Map of NH datatype to Breeze datatype.
        static Dictionary<string, string> BreezeTypeMap = new Dictionary<string, string>() {
                    {"Byte[]", "Binary" },
                    {"BinaryBlob", "Binary" },
                    {"Timestamp", "DateTime" },
                    {"TimeAsTimeSpan", "Time" }
                };


        // Map of data type to Breeze validation type
        static Dictionary<string, string> ValidationTypeMap = new Dictionary<string, string>() {
                    {"Boolean", "bool" },
                    {"Byte", "byte" },
                    {"DateTime", "date" },
                    {"DateTimeOffset", "date" },
                    {"Decimal", "number" },
                    {"Guid", "guid" },
                    {"Int16", "int16" },
                    {"Int32", "int32" },
                    {"Int64", "integer" },
                    {"Single", "number" },
                    {"Time", "duration" },
                    {"TimeAsTimeSpan", "duration" }
                };
        

    }


}
