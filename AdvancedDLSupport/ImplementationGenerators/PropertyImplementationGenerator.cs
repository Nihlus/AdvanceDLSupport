﻿using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

namespace AdvancedDLSupport.ImplementationGenerators
{
    /// <summary>
    /// Generates implementations for properties.
    /// </summary>
    internal class PropertyImplementationGenerator : ImplementationGeneratorBase<PropertyInfo>
    {
        private const MethodAttributes PropertyMethodAttributes =
            MethodAttributes.PrivateScope |
            MethodAttributes.Public |
            MethodAttributes.Virtual |
            MethodAttributes.HideBySig |
            MethodAttributes.VtableLayoutMask |
            MethodAttributes.SpecialName;

        /// <summary>
        /// Initializes a new instance of the <see cref="PropertyImplementationGenerator"/> class.
        /// </summary>
        /// <param name="targetModule">The module in which the property implementation should be generated.</param>
        /// <param name="targetType">The type in which the property implementation should be generated.</param>
        /// <param name="targetTypeConstructorIL">The IL generator for the target type's constructor.</param>
        public PropertyImplementationGenerator(ModuleBuilder targetModule, TypeBuilder targetType, ILGenerator targetTypeConstructorIL)
            : base(targetModule, targetType, targetTypeConstructorIL)
        {
        }

        /// <inheritdoc />
        public override void GenerateImplementation(PropertyInfo property)
        {
            var uniqueIdentifier = Guid.NewGuid().ToString().Replace("-", "_");

            // Note, the field is going to have to be a pointer, because it is pointing to global variable
            var propertyFieldBuilder = TargetType.DefineField
            (
                $"{property.Name}_{uniqueIdentifier}",
                typeof(IntPtr),
                FieldAttributes.Private
            );

            var propertyBuilder = TargetType.DefineProperty
            (
                property.Name,
                PropertyAttributes.None,
                CallingConventions.HasThis,
                property.PropertyType,
                property.GetIndexParameters().Select(p => p.ParameterType).ToArray()
            );

            // TODO: Refactor? On one hand, it doesn't take much computational power to process the below
            // On other hand, it isn't as pretty as other code...
            if (property.PropertyType.IsPointer)
            {
                if (property.CanRead)
                {
                    // Return a Pointer from Global Variable
                    GeneratePointerPropertyGetter(property, propertyFieldBuilder, propertyBuilder);
                }

                if (property.CanWrite)
                {
                    // Write new address for Global Variable
                    GeneratePointerPropertySetter(property, propertyFieldBuilder, propertyBuilder);
                }
            }
            else if (property.PropertyType.IsArray)
            {
                if (property.CanRead)
                {
                    GenerateArrayPropertyGetter(property, propertyFieldBuilder, propertyBuilder);
                }

                if (property.CanWrite)
                {
                    GenerateArrayPropertySetter(property, propertyFieldBuilder, propertyBuilder);
                }
            }
            else
            {
                if (property.CanRead)
                {
                    GenerateNonArrayPropertyGetter(property, propertyFieldBuilder, propertyBuilder);
                }

                if (property.CanWrite)
                {
                    GenerateNonArrayPropertySetter(property, propertyFieldBuilder, propertyBuilder);
                }
            }

            PropertyInitializationInConstructor(property, propertyFieldBuilder); // This is ok for all 3 types of properties.
        }

        private void PropertyInitializationInConstructor(PropertyInfo property, FieldInfo propertyFieldBuilder)
        {
            var loadSymbolMethod = typeof(AnonymousImplementationBase).GetMethod
            (
                "LoadSymbol",
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            TargetTypeConstructorIL.Emit(OpCodes.Ldarg_0);
            TargetTypeConstructorIL.Emit(OpCodes.Ldarg_0);
            TargetTypeConstructorIL.Emit(OpCodes.Ldstr, property.Name);
            TargetTypeConstructorIL.EmitCall(OpCodes.Call, loadSymbolMethod, null);
            TargetTypeConstructorIL.Emit(OpCodes.Stfld, propertyFieldBuilder);
        }

        private void GeneratePointerPropertySetter(PropertyInfo property, FieldInfo propertyFieldBuilder, PropertyBuilder propertyBuilder)
        {
            var actualSetMethod = property.GetSetMethod();
            var setterMethod = TargetType.DefineMethod
            (
                actualSetMethod.Name,
                PropertyMethodAttributes,
                actualSetMethod.CallingConvention,
                typeof(void),
                actualSetMethod.GetParameters().Select(p => p.ParameterType).ToArray()
            );

            var writeIntPtrFunc = typeof(Marshal).GetMethods().First
            (
                m =>
                    m.Name == nameof(Marshal.WriteIntPtr) &&
                    m.GetParameters().Length == 3
            );

            var explicitConvertToIntPtrFunc = typeof(IntPtr).GetMethods().First
            (
                m =>
                    m.Name == "op_Explicit"
            );

            var setterIL = setterMethod.GetILGenerator();
            setterIL.Emit(OpCodes.Ldarg_0);                     // Push this reference to load symbol address
            setterIL.Emit(OpCodes.Ldfld, propertyFieldBuilder); // Push Symbol address to stack
            setterIL.Emit(OpCodes.Ldc_I4, 0);                   // Push 0 offset to stack
            setterIL.Emit(OpCodes.Ldarg_1);                     // Push value to stack

            setterIL.EmitCall(OpCodes.Call, explicitConvertToIntPtrFunc, null); // Explicit Convert Pointer to IntPtr object

            setterIL.EmitCall
            (
                OpCodes.Call,
                writeIntPtrFunc,
                null
            );

            setterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetSetMethod(setterMethod);
        }

        private void GeneratePointerPropertyGetter(PropertyInfo property, FieldInfo propertyFieldBuilder, PropertyBuilder propertyBuilder)
        {
            var actualGetMethod = property.GetGetMethod();
            var getterMethod = TargetType.DefineMethod
            (
                actualGetMethod.Name,
                PropertyMethodAttributes,
                actualGetMethod.CallingConvention,
                actualGetMethod.ReturnType,
                Type.EmptyTypes
            );

            var readIntPtrFunc = typeof(Marshal).GetMethods().First
            (
                m =>
                    m.Name == nameof(Marshal.ReadIntPtr) &&
                    m.GetParameters().Length == 1
            );

            var getterIL = getterMethod.GetILGenerator();
            getterIL.Emit(OpCodes.Ldarg_0);                     // Push this reference so Symbol pointer can be loaded
            getterIL.Emit(OpCodes.Ldfld, propertyFieldBuilder); // Push symbol pointer to stack

            getterIL.EmitCall // Dereference the symbol pointer to get pointer information
            (
                OpCodes.Call,
                readIntPtrFunc,
                null
            );

            getterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetGetMethod(getterMethod);
        }

        private void GenerateArrayPropertySetter(PropertyInfo property, FieldInfo propertyFieldBuilder, PropertyBuilder propertyBuilder)
        {
            var actualSetMethod = property.GetSetMethod();
            var setterMethod = TargetType.DefineMethod
            (
                actualSetMethod.Name,
                PropertyMethodAttributes,
                actualSetMethod.CallingConvention,
                typeof(void),
                actualSetMethod.GetParameters().Select(p => p.ParameterType).ToArray()
            );

            var structToPtrFunc = typeof(Marshal).GetMethods().First
            (
                m =>
                    m.Name == nameof(Marshal.StructureToPtr) &&
                    m.GetParameters().Length == 3 &&
                    m.IsGenericMethod
            );

            var setterIL = setterMethod.GetILGenerator();
            setterIL.Emit(OpCodes.Ldarg_1);
            setterIL.Emit(OpCodes.Ldarg_0);
            setterIL.Emit(OpCodes.Ldfld, propertyFieldBuilder);
            setterIL.Emit(OpCodes.Ldc_I4, 0); // false for deleting structure that is already stored in pointer
            setterIL.EmitCall
            (
                OpCodes.Call,
                structToPtrFunc.MakeGenericMethod(property.PropertyType),
                null
            );

            setterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetSetMethod(setterMethod);
        }

        private void GenerateArrayPropertyGetter(PropertyInfo property, FieldInfo propertyFieldBuilder, PropertyBuilder propertyBuilder)
        {
            throw new NotImplementedException("Still a work in progress.");
            var actualGetMethod = property.GetGetMethod();
            var getterMethod = TargetType.DefineMethod
            (
                actualGetMethod.Name,
                PropertyMethodAttributes,
                actualGetMethod.CallingConvention,
                actualGetMethod.ReturnType,
                Type.EmptyTypes
            );

            var ptrToStructFunc = typeof(Marshal).GetMethods().First
            (
                m =>
                    m.Name == nameof(Marshal.PtrToStructure) &&
                    m.GetParameters().Length == 1 &&
                    m.IsGenericMethod
            );

            var getterIL = getterMethod.GetILGenerator();
            getterIL.Emit(OpCodes.Ldarg_0);
            getterIL.Emit(OpCodes.Ldfld, propertyFieldBuilder);
            getterIL.EmitCall
            (
                OpCodes.Call,
                ptrToStructFunc.MakeGenericMethod(property.PropertyType),
                null
            );

            getterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetGetMethod(getterMethod);
        }

        private void GenerateNonArrayPropertySetter(PropertyInfo property, FieldInfo propertyFieldBuilder, PropertyBuilder propertyBuilder)
        {
            throw new NotImplementedException("Still a work in progress.");

            var actualSetMethod = property.GetSetMethod();
            var setterMethod = TargetType.DefineMethod
            (
                actualSetMethod.Name,
                PropertyMethodAttributes,
                actualSetMethod.CallingConvention,
                typeof(void),
                actualSetMethod.GetParameters().Select(p => p.ParameterType).ToArray()
            );

            var structToPtrFunc = typeof(Marshal).GetMethods().First
            (
                m =>
                    m.Name == nameof(Marshal.StructureToPtr) &&
                    m.GetParameters().Length == 3 &&
                    m.IsGenericMethod
            );

            var setterIL = setterMethod.GetILGenerator();
            setterIL.Emit(OpCodes.Ldarg_1);
            setterIL.Emit(OpCodes.Ldarg_0);
            setterIL.Emit(OpCodes.Ldfld, propertyFieldBuilder);
            setterIL.Emit(OpCodes.Ldc_I4, 0); // false for deleting structure that is already stored in pointer
            setterIL.EmitCall
            (
                OpCodes.Call,
                structToPtrFunc.MakeGenericMethod(property.PropertyType),
                null
            );

            setterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetSetMethod(setterMethod);
        }

        private void GenerateNonArrayPropertyGetter(PropertyInfo property, FieldInfo propertyFieldBuilder, PropertyBuilder propertyBuilder)
        {
            var actualGetMethod = property.GetGetMethod();
            var getterMethod = TargetType.DefineMethod
            (
                actualGetMethod.Name,
                PropertyMethodAttributes,
                actualGetMethod.CallingConvention,
                actualGetMethod.ReturnType,
                Type.EmptyTypes
            );

            var ptrToStructFunc = typeof(Marshal).GetMethods().First
            (
                m =>
                    m.Name == nameof(Marshal.PtrToStructure) &&
                    m.GetParameters().Length == 1 &&
                    m.IsGenericMethod
            );

            var getterIL = getterMethod.GetILGenerator();
            getterIL.Emit(OpCodes.Ldarg_0);
            getterIL.Emit(OpCodes.Ldfld, propertyFieldBuilder);
            getterIL.EmitCall
            (
                OpCodes.Call,
                ptrToStructFunc.MakeGenericMethod(property.PropertyType),
                null
            );

            getterIL.Emit(OpCodes.Ret);

            propertyBuilder.SetGetMethod(getterMethod);
        }
    }
}
