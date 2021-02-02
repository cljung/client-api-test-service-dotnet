const required_properties = [
    "id",
    "display",
    "input"
]

const required_display_properties = [
    "id",
    "locale",
    "contract",
    "card",
    "consent",
    "claims"
]

const required_card_properties = [
    "title",
    "issuedBy",
    "backgroundColor",
    "textColor",
    "logo",
    "description"
]

const required_consent_properties = [
    "title",
    "instructions"
]

const required_input_properties = [
    "id",
    "credentialIssuer",
    "issuer",
    "attestations"
]

function fetchAndValidateContract() {

    try {

        var contractValidationElement = document.getElementById("contractUrlForValidation")
        if (contractValidationElement == null | !contractValidationElement.value.startsWith("https://")) {
            throw Error("Invalid Contract Url")
        }
        const url = contractValidationElement.value

        fetch(url)
        .then(handleServiceErrors)
        .then((response) => {
        response.json().then((contract) => {
            const results = validateContract(contract)
            console.log(results)
            if (results.error != null) {
                displayFailedValidation("Validation Failed: " + results.error)
            } else if (results.missingProperties == null | results.missingProperties.length > 0) {
                displayFailedValidation("Validation Failed: missing propertie(s) - " + JSON.stringify(results.missingProperties))
            } else {
                displaySuccessfulValidation()
            }
        })
        }).catch((error) => {
        console.log("Unable to Fetch Contract")
        alert("Unable to Fetch Contract. Please check url.")
        });
    } catch (error) {
        console.log("Unable to Fetch Contract")
        alert("Unable to Fetch Contract. Please check url.")
    }
}

function validateContract(contract) {
    let missingProperties = []
    required_properties.forEach((property) => {
        missingProperties = validateProperty(contract, property, missingProperties)
    })
    // return if top level property is missing
    if (missingProperties.length) {
        return {
            missingProperties
        }
    }
    required_display_properties.forEach((property) => {
        missingProperties = validateProperty(contract.display, property, missingProperties)
    })
    required_card_properties.forEach((property) => {
        missingProperties = validateProperty(contract.display.card, property, missingProperties)
    })
    required_consent_properties.forEach((property) => {
        missingProperties = validateProperty(contract.display.consent, property, missingProperties)
    })
    required_input_properties.forEach((property) => {
        missingProperties = validateProperty(contract.input, property, missingProperties)
    })
    return { 
        missingProperties
    }
}

function validateProperty(json, property, missingProperties) {
    if (json[property] == null) {
        missingProperties.push(property)
    }
    return missingProperties
}

function handleServiceErrors(response) {
    if (response.ok) return response;
    throw response;
}

function displaySuccessfulValidation() {
    let resultText = document.getElementById("validationResult")
    resultText.className = "lead"
    resultText.style.color = "green"
    resultText.innerText = "Validation Successful"
}

function displayFailedValidation(results) {
    let resultText = document.getElementById("validationResult")
    resultText.className = "lead"
    resultText.style.color = "red"
    resultText.innerText = "Missing Properties: " + results
}