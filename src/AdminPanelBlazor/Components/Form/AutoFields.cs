﻿// <copyright file="AutoFields.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AdminPanelBlazor.Components.Form
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using Microsoft.AspNetCore.Components;
    using Microsoft.AspNetCore.Components.Forms;
    using Microsoft.AspNetCore.Components.Rendering;

    /// <summary>
    /// A razor component which automatically generates input fields for all properties for the type of the enclosing form model.
    /// Must be used inside a <see cref="EditForm"/>.
    /// </summary>
    public class AutoFields : ComponentBase
    {
        /// <summary>
        /// Gets or sets the context of the <see cref="EditForm"/>.
        /// </summary>
        /// <value>
        /// The context.
        /// </value>
        [CascadingParameter]
        public EditContext Context { get; set; }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            int i = 0;
            foreach (var propertyInfo in this.Context.Model.GetType()
                .GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!(propertyInfo.GetCustomAttribute<BrowsableAttribute>()?.Browsable ?? true))
                {
                    return;
                }

                if (propertyInfo.PropertyType == typeof(string))
                {
                    this.BuildField<string, TextField>(propertyInfo, builder, ref i);
                }
                else if (propertyInfo.PropertyType == typeof(int))
                {
                    this.BuildField<int, NumberField>(propertyInfo, builder, ref i);
                }
                else if (propertyInfo.PropertyType == typeof(bool))
                {
                    this.BuildField<bool, BooleanField>(propertyInfo, builder, ref i);
                }
                else if (propertyInfo.PropertyType == typeof(DateTime))
                {
                    this.BuildField<DateTime, DateField>(propertyInfo, builder, ref i);
                }
                else if (propertyInfo.PropertyType.IsEnum)
                {
                    var method = this.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                        .Where(m => m.Name == nameof(this.BuildField))
                        .First(m => m.ContainsGenericParameters && m.GetGenericArguments().Length == 2)
                        .MakeGenericMethod(propertyInfo.PropertyType, typeof(EnumField<>).MakeGenericType(propertyInfo.PropertyType));
                    var parameters = new object[] {propertyInfo, builder, i};
                    method.Invoke(this, parameters);
                    i = (int)parameters[2];
                }
                else if (propertyInfo.PropertyType.IsArray)
                {
                    // not supported.
                }
                else if (propertyInfo.PropertyType.IsClass && !propertyInfo.Name.StartsWith("Raw"))
                {
                    i = this.BuildLookUpField(builder, propertyInfo, i);
                }
                else if (propertyInfo.PropertyType.IsInterface
                         && propertyInfo.PropertyType.IsGenericType
                         && propertyInfo.PropertyType.GetGenericTypeDefinition() == typeof(IList<>))
                {
                    i = this.BuildMultiLookUpField(builder, propertyInfo, i);
                }
            }
        }

        private int BuildLookUpField(RenderTreeBuilder builder, PropertyInfo propertyInfo, int i)
        {
            var method = this.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == nameof(this.BuildField))
                .First(m => m.ContainsGenericParameters && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(propertyInfo.PropertyType, typeof(LookupField<>).MakeGenericType(propertyInfo.PropertyType));
            var parameters = new object[] { propertyInfo, builder, i };
            method.Invoke(this, parameters);
            i = (int)parameters[2];
            return i;
        }

        private int BuildMultiLookUpField(RenderTreeBuilder builder, PropertyInfo propertyInfo, int i)
        {
            var method = this.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == nameof(this.BuildField))
                .First(m => m.ContainsGenericParameters && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(propertyInfo.PropertyType, typeof(MultiLookupField<>).MakeGenericType(propertyInfo.PropertyType.GenericTypeArguments[0]));
            var parameters = new object[] { propertyInfo, builder, i };
            method.Invoke(this, parameters);
            i = (int)parameters[2];
            return i;
        }

        private void BuildField<TValue, TComponent>(PropertyInfo propertyInfo, RenderTreeBuilder builder, ref int i)
        {
            this.BuildField<TValue>(typeof(TComponent), propertyInfo, builder, ref i);
        }

        private void BuildField<TValue>(Type componentType, PropertyInfo propertyInfo, RenderTreeBuilder builder, ref int i)
        {
            builder.OpenComponent(i++, componentType);
            try
            {
                builder.AddAttribute(i++, "ValueExpression", this.CreateExpression<TValue>(propertyInfo));
                builder.AddAttribute(i++, "Value", propertyInfo.GetValue(this.Context.Model));
                builder.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<TValue>(this, EventCallback.Factory.Create<TValue>(
                    this,
                    value =>
                    {
                        if (propertyInfo.SetMethod is { })
                        {
                            propertyInfo.SetValue(this.Context.Model, value);
                        }
                    })));
            }
            finally
            {
                builder.CloseComponent();
            }
        }

        private Expression<Func<T>> CreateExpression<T>(PropertyInfo propertyInfo)
        {
            var classType = this.Context.Model.GetType();
            var constantExpr = Expression.Constant(this.Context.Model, classType);
            var memberExpr = Expression.Property(constantExpr, propertyInfo.Name);
            var delegateType = typeof(Func<>).MakeGenericType(typeof(T));
            return (Expression<Func<T>>)Expression.Lambda(delegateType, memberExpr);
        }
    }
}
