using GraphBridge.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GraphBridge.EntityFrameworkCore.Infrastructure;

public sealed class Neo4jModelValidator(ModelValidatorDependencies dependencies) : ModelValidator(dependencies)
{
    public override void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        GraphModelValidation.Validate(model);
        base.Validate(model, logger);
    }
}
