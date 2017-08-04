using ProtoBuf.Meta;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ProtoBuf.Issues
{
    public class SO45323448
    {
        [Fact]
        public void OldSchoolParseableWorks_Class()
        {
            var model = TypeModel.Create();
            model.AutoCompile = false;
            model.AllowParseableTypes = true;

            var obj = new Haz<HazParseClass> { X = new HazParseClass { Value = "abcdef" } };
            Assert.Equal("abcdef", obj.X?.ToString());

            var clone = (Haz<HazParseClass>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X?.Value);

            model.CompileInPlace();
            clone = (Haz<HazParseClass>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X?.Value);

            clone = (Haz<HazParseClass>)(model.Compile()).DeepClone(obj);
            Assert.Equal("abcdef", clone?.X?.Value);

            using (var ms = new MemoryStream())
            {
                model.Serialize(ms, obj);
                ms.Position = 0;
                var asString = (Haz<string>)model.Deserialize(ms, null, typeof(Haz<string>));
                Assert.Equal("abcdef", asString.X);
            }
        }

        [Fact]
        public void OldSchoolParseableWorks_Struct()
        {
            var model = TypeModel.Create();
            model.AutoCompile = false;
            model.AllowParseableTypes = true;

            var obj = new Haz<HazParseStruct> { X = new HazParseStruct("abcdef") };
            Assert.Equal("abcdef", obj.X.ToString());

            var clone = (Haz<HazParseStruct>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X.Value);

            model.CompileInPlace();
            clone = (Haz<HazParseStruct>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X.Value);

            clone = (Haz<HazParseStruct>)(model.Compile()).DeepClone(obj);
            Assert.Equal("abcdef", clone?.X.Value);

            using (var ms = new MemoryStream())
            {
                model.Serialize(ms, obj);
                ms.Position = 0;
                var asString = (Haz<string>)model.Deserialize(ms, null, typeof(Haz<string>));
                Assert.Equal("abcdef", asString.X);
            }
        }

        [Fact]
        public void OperatorParseableWorks_Class()
        {
            var model = TypeModel.Create();
            model.AutoCompile = false;
            model.AllowParseableTypes = false;
            model[typeof(HazOperatorsClass)].SetSurrogate(typeof(string));

            var obj = new Haz<HazOperatorsClass> { X = new HazOperatorsClass { Value = "abcdef" } };
            Assert.Equal("abcdef", (string)obj.X);

            var clone = (Haz<HazOperatorsClass>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X?.Value);

            model[obj.GetType()].CompileInPlace();
            clone = (Haz<HazOperatorsClass>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X?.Value);

            //clone = (Haz<HazOperatorsClass>)(model.Compile()).DeepClone(obj);
            Assert.Equal("abcdef", clone?.X?.Value);

            using (var ms = new MemoryStream())
            {
                model.Serialize(ms, obj);
                ms.Position = 0;
                var asString = (Haz<string>)model.Deserialize(ms, null, typeof(Haz<string>));
                Assert.Equal("abcdef", asString.X);
            }
        }

        [Fact]
        public void OperatorParseableWorks_Struct()
        {
            var model = TypeModel.Create();
            model.AutoCompile = false;
            model.AllowParseableTypes = false;
            model[typeof(HazOperatorsStruct)].SetSurrogate(typeof(string));

            var obj = new Haz<HazOperatorsStruct> { X = new HazOperatorsStruct("abcdef") };
            Assert.Equal("abcdef", (string)obj.X);

            var clone = (Haz<HazOperatorsStruct>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X.Value);

            model.CompileInPlace();
            clone = (Haz<HazOperatorsStruct>)model.DeepClone(obj);
            Assert.Equal("abcdef", clone?.X.Value);

            clone = (Haz<HazOperatorsStruct>)(model.Compile()).DeepClone(obj);
            Assert.Equal("abcdef", clone?.X.Value);

            using (var ms = new MemoryStream())
            {
                model.Serialize(ms, obj);
                ms.Position = 0;
                var asString = (Haz<string>)model.Deserialize(ms, null, typeof(Haz<string>));
                Assert.Equal("abcdef", asString.X);
            }
        }


        [ProtoContract]
        public class Haz<T>
        {
            [ProtoMember(1)]
            public T X { get; set; }
        }

        public class HazParseClass
        {
            public string Value { get; set; }
            public override string ToString() => Value;
            public static HazParseClass Parse(string val)
                => val == null ? null : (new HazParseClass { Value = val });
        }

        public class HazOperatorsClass
        {
            public string Value { get; set; }
            public static implicit operator string (HazOperatorsClass val) => val?.Value;
            public static explicit operator HazOperatorsClass(string val)
                => val == null ? null : (new HazOperatorsClass {  Value = val });
        }

        public struct HazParseStruct
        {
            public HazParseStruct(string value) { Value = value; }
            public string Value { get; }
            public override string ToString() => Value;
            public static HazParseStruct Parse(string val) => new HazParseStruct(val);
        }

        public struct HazOperatorsStruct
        {
            public HazOperatorsStruct(string value) { Value = value; }
            public string Value { get; }
            public static implicit operator string(HazOperatorsStruct val) => val.Value;
            public static explicit operator HazOperatorsStruct(string val) => new HazOperatorsStruct(val);
        }
    }
}
