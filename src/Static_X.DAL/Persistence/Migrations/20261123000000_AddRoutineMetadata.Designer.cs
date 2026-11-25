using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DAL.Migrations;

[DbContext(typeof(StaticXDbContext))]
[Migration("20261123000000_AddRoutineMetadata")]
partial class AddRoutineMetadata
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildRoutineMetadataModel(modelBuilder);

    internal static void BuildRoutineMetadataModel(ModelBuilder modelBuilder)
    {
        AddStudentRoutines.BuildRoutineModel(modelBuilder);

        modelBuilder.Entity("Entity.StudentRoutine", routine =>
        {
            routine.Property<string>("Instructions").HasMaxLength(2000).HasColumnType("nvarchar(2000)");
            routine.Property<string>("Title").HasMaxLength(200).HasColumnType("nvarchar(200)");
        });

        modelBuilder.Entity("Entity.StudentRoutineDay", day =>
        {
            day.Property<string>("Instructions").HasMaxLength(2000).HasColumnType("nvarchar(2000)");
            day.Property<string>("Name").HasMaxLength(200).HasColumnType("nvarchar(200)");
        });
    }
}
