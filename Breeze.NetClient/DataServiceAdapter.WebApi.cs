﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Breeze.Core;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace Breeze.NetClient {
  public class WebApiDataServiceAdapter : IDataServiceAdapter {

    public String Name {
      get { return "WebApi"; }
    }

    public async Task<SaveResult> SaveChanges(IEnumerable<IEntity> entitiesToSave, SaveOptions saveOptions) {
      var entityManager = entitiesToSave.First().EntityAspect.EntityManager;
      var saveBundleNode = PrepareSaveBundle(entitiesToSave, saveOptions);
      try {
        var saveResultJson = await saveOptions.DataService.PostAsync(saveOptions.ResourceName, saveBundleNode.Serialize());
        return ProcessSaveResult(entityManager, saveResultJson);
      } catch (HttpRequestException e) {
        throw SaveException.Parse(e.Message);
      }
    }

    private SaveResult ProcessSaveResult(EntityManager entityManager, string saveResultJson) {


      var jo = JObject.Parse(saveResultJson);

      var jn = new JNode(jo);
      var kms = jn.GetArray<KeyMapping>("KeyMappings");
      var keyMappings = kms.Select(km => ToEntityKeys(km)).ToDictionary(tpl => tpl.Item1, tpl => tpl.Item2);
      using (entityManager.NewIsLoadingBlock()) {
        keyMappings.ForEach(km => {
          var targetEntity = entityManager.FindEntityByKey(km.Key);
          targetEntity.EntityAspect.SetDpValue(km.Key.EntityType.KeyProperties[0], km.Value.Values[0]);
        });

        var prop = jo.Property("Entities");
        if (prop == null) return null;
        var entityNodes = (JArray)prop.Value;
        var serializer = new JsonSerializer();
        var jsonConverter = new JsonEntityConverter(entityManager, MergeStrategy.OverwriteChanges, NormalizeEntityTypeName);
        serializer.Converters.Add(jsonConverter);
        var entities = serializer.Deserialize<IEnumerable<IEntity>>(entityNodes.CreateReader());
        entities.ForEach(e => e.EntityAspect.AcceptChanges());
        return new SaveResult(entities, keyMappings);
      }
    
    }

    private Tuple<EntityKey, EntityKey> ToEntityKeys(KeyMapping keyMapping) {
      var entityTypeName = NormalizeEntityTypeName(keyMapping.EntityTypeName);
      var et = MetadataStore.Instance.GetEntityType(entityTypeName);
      var oldKey = new EntityKey(et, keyMapping.TempValue);
      var newKey = new EntityKey(et, keyMapping.RealValue);
      return Tuple.Create(oldKey, newKey);
    }

    private JNode PrepareSaveBundle(IEnumerable<IEntity> entitiesToSave, SaveOptions saveOptions) {
      var jn = new JNode();
      jn.AddArray("entities", entitiesToSave.Select(e => EntityToJNode(e)));
      jn.AddJNode("saveOptions", saveOptions);
      return jn;
    }

    private String NormalizeEntityTypeName(String clrTypeName) {
      if (String.IsNullOrEmpty(clrTypeName)) return null;

      var entityTypeNameNoAssembly = clrTypeName.Split(',')[0];
      var nameParts = entityTypeNameNoAssembly.Split('.');
      String ns;
      var shortName = nameParts[nameParts.Length - 1];
      if (nameParts.Length > 1) {
        ns = String.Join(".", nameParts.Take(nameParts.Length - 1));
      } else {
        ns = "";
      }
      var typeName = StructuralType.QualifyTypeName(shortName, ns);
      return typeName;
    }

    private JNode EntityToJNode(IStructuralObject so) {
      var entity = so as IEntity;
      JNode jn;
      if (entity != null) {
        var entityAspect = entity.EntityAspect;
        jn = DataToJNode(entityAspect);
        jn.AddJNode("entityAspect", BuildEntityAspectNode(entityAspect));
      } else {
        var complexAspect = ((IComplexObject)so).ComplexAspect;
        jn = DataToJNode(complexAspect);
      }
      return jn;      
    }

    private JNode DataToJNode(StructuralAspect aspect) {
      var jn = new JNode();
      var stype = aspect.StructuralType;

      stype.DataProperties.ForEach(dp => {
        var val = aspect.GetValue(dp.Name);
        // handle nonscalar dps
        if (dp.IsComplexProperty) {
          jn.AddJNode(dp.NameOnServer, DataToJNode(((IComplexObject)val).ComplexAspect));
        } else {
          jn.AddPrimitive(dp.NameOnServer, val, dp.DefaultValue);
        }
      });
      return jn;
    }

    private JNode BuildEntityAspectNode(EntityAspect entityAspect) {
      var nc = MetadataStore.Instance.NamingConvention;
      var jn = new JNode();
      var entityType = entityAspect.EntityType;
        
      jn.AddPrimitive("entityTypeName", entityType.Name);
      jn.AddEnum("entityState", entityAspect.EntityState);
      jn.AddPrimitive("defaultResourceName", entityType.DefaultResourceName);
      jn.AddJNode("originalValuesMap", BuildOriginalValuesMapNode(entityAspect, nc));
      var agkType = entityType.AutoGeneratedKeyType;
      if (agkType != AutoGeneratedKeyType.None) {
        var agkNode = new JNode();
        agkNode.AddPrimitive("propertyName", entityType.KeyProperties[0].Name);
        agkNode.AddEnum("autoGeneratedKeyType", agkType);
        jn.AddJNode("autoGeneratedKey", agkNode);
      }
      return jn;
    }

    private JNode BuildOriginalValuesMapNode(StructuralAspect aspect, NamingConvention nc) {
      var ovMap = aspect.OriginalValuesMap.ToDictionary(kvp => nc.ClientPropertyNameToServer(kvp.Key), kvp => kvp.Value);
      var cps = aspect is EntityAspect 
        ? ((EntityAspect) aspect).EntityType.ComplexProperties 
        : ((ComplexAspect) aspect).ComplexType.ComplexProperties;
        
      cps.ForEach(cp => {
        var co = aspect.GetValue(cp.Name);
        var serverName = nc.ClientPropertyNameToServer(cp.Name);
        if (cp.IsScalar) {
          var ovmNode = BuildOriginalValuesMapNode( ((IComplexObject) co).ComplexAspect, nc);
          ovMap[serverName] = ovmNode;
        } else {
          var ovmNodes = ((IEnumerable) co).Cast<IComplexObject>().Select(co2 => BuildOriginalValuesMapNode(co2.ComplexAspect, nc));
          ovMap[serverName] = JNode.ToJArray(ovmNodes);
        }
      });
      var result = JNode.BuildMapNode(ovMap);
      return result;
        
      
    }
  }



  public class KeyMapping {
    public String EntityTypeName;
    public Object TempValue;
    public Object RealValue;
  }

  public class EntityError {

    public String ErrorName;
    public String EntityTypeName;
    public Object[] KeyValues;
    public String PropertyName;
    public string ErrorMessage;

  }
 
}
