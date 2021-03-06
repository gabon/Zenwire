﻿using System;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.Metadata.Edm;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Zenwire.Extensions
{
    [ExcludeFromCodeCoverage]
    public static class DbContextExtensions
    {
        public static T AddOrUpdate<T>(this DbContext context, T entity)
            where T : class
        {
            if (context == null) throw new ArgumentNullException("context");
            if (entity == null) throw new ArgumentNullException("entity");

            if (IsTransient(context, entity))
            {
                context.Set<T>().Add(entity);
            }
            else
            {
                context.Set<T>().Attach(entity);
                context.Entry(entity).State = EntityState.Modified;
            }
            return entity;
        }

        public static bool IsTransient<T>(this DbContext context, T entity)
            where T : class
        {
            if (context == null) throw new ArgumentNullException("context");
            if (entity == null) throw new ArgumentNullException("entity");

            var propertyInfo = FindPrimaryKeyProperty<T>(context);
            var propertyType = propertyInfo.PropertyType;
            //what's the default value for the type?
            var transientValue = propertyType.IsValueType ?
                Activator.CreateInstance(propertyType) : null;
            //is the pk the same as the default value (int == 0, string == null ...)
            return Equals(propertyInfo.GetValue(entity, null), transientValue);
        }

        public static T Load<T>(this DbContext context, object id)
             where T : class
        {
            if (context == null) throw new ArgumentNullException("context");
            if (id == null) throw new ArgumentNullException("id");

            var property = FindPrimaryKeyProperty<T>(context);
            //check to see if it's already loaded (slow if large numbers loaded)
            var entity = context.Set<T>().Local
                .FirstOrDefault(x => id.Equals(property.GetValue(x, null)));
            if (entity == null)
            {
                //it's not loaded, just create a stub with only primary key set
                entity = CreateEntity<T>(id, property);

                context.Set<T>().Attach(entity);
            }
            return entity;
        }

        public static bool IsLoaded<T>(this DbContext context, object id)
            where T : class
        {
            if (context == null) throw new ArgumentNullException("context");
            if (id == null) throw new ArgumentNullException("id");

            var property = FindPrimaryKeyProperty<T>(context);
            //check to see if it's already loaded (slow if large numbers loaded)
            var entity = context.Set<T>().Local
                .FirstOrDefault(x => id.Equals(property.GetValue(x, null)));
            return entity != null;
        }

        public static void MarkReferencesUnchanged<T>(DbContext context, T entity)
            where T : class
        {
            var objectContext = ((IObjectContextAdapter)context).ObjectContext;
            var objectSet = objectContext.CreateObjectSet<T>();
            var elementType = objectSet.EntitySet.ElementType;
            var navigationProperties = elementType.NavigationProperties;
            
            //the references
            var references = from navigationProperty in navigationProperties
                             let end = navigationProperty.ToEndMember
                             where end.RelationshipMultiplicity == RelationshipMultiplicity.ZeroOrOne ||
                             end.RelationshipMultiplicity == RelationshipMultiplicity.One
                             select navigationProperty.Name;
            
            //NB: We don't check Collections. EF wants to handle the object graph.
            var parentEntityState = context.Entry(entity).State;
            foreach (var navigationProperty in references)
            {
                //if it's modified but not loaded, don't need to touch it
                if (parentEntityState == EntityState.Modified &&
                    !context.Entry(entity).Reference(navigationProperty).IsLoaded)
                    continue;
                var propertyInfo = typeof(T).GetProperty(navigationProperty);
                var value = propertyInfo.GetValue(entity, null);
                context.Entry(value).State = EntityState.Unchanged;
            }
        }

        public static T Merge<T>(this DbContext context, object dataTransferObject)
             where T : class
        {
            if (context == null) throw new ArgumentNullException("context");
            if (dataTransferObject == null) throw new ArgumentNullException("dataTransferObject");

            var property = FindPrimaryKeyProperty<T>(context);
            //find the id property of the dto
            var idProperty = dataTransferObject.GetType().GetProperty(property.Name);
            if (idProperty == null)
                throw new InvalidOperationException("Cannot find an id on the dataTransferObject");
            var id = idProperty.GetValue(dataTransferObject, null);
            //has the id been set (existing item) or not (transient)?
            var propertyType = property.PropertyType;
            var transientValue = propertyType.IsValueType ?
                Activator.CreateInstance(propertyType) : null;
            var isTransient = Equals(id, transientValue);
            T entity;
            if (isTransient)
            {
                //it's transient, just create a dummy
                entity = CreateEntity<T>(id, property);
                //if DatabaseGeneratedOption(DatabaseGeneratedOption.None) and no id, this errors
                context.Set<T>().Attach(entity);
            }
            else
            {
                //try to load from identity map or database
                entity = context.Set<T>().Find(id);
                if (entity == null)
                {
                    //could not find entity, assume assigned primary key
                    entity = CreateEntity<T>(id, property);
                    context.Set<T>().Add(entity);
                }
            }
            //copy the values from DTO onto the entry
            context.Entry(entity).CurrentValues.SetValues(dataTransferObject);
            return entity;
        }


        private static PropertyInfo FindPrimaryKeyProperty<T>(IObjectContextAdapter context)
            where T : class
        {
            //find the primary key
            var objectContext = context.ObjectContext;
            //this will error if it's not a mapped entity
            var objectSet = objectContext.CreateObjectSet<T>();
            var elementType = objectSet.EntitySet.ElementType;
            var pk = elementType.KeyMembers.First();
            //look it up on the entity
            var propertyInfo = typeof(T).GetProperty(pk.Name);
            return propertyInfo;
        }

        private static T CreateEntity<T>(object id, PropertyInfo property)
            where T : class
        {
            // consider IoC here
            var entity = (T)Activator.CreateInstance(typeof(T));
            //set the value of the primary key (may error if wrong type)
            property.SetValue(entity, id, null);
            return entity;
        }
    }
}