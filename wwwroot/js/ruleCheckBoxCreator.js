
// function getRules() {
//     fetch('/rules')
//     .then(handleServiceErrors)
//     .then((response) => {
//       response.json().then((content) => {
//         // var contractDetailsDiv = document.getElementById("contractDetails")
//         // contractDetailsDiv.appendChild(grid(content))
//         localStorage.setItem("rules", JSON.stringify(content))
//         console.log(content)
//         return content
//       })
//     }).catch((error) => {
//       console.log("No Request Rules Found.")
//     });
// }

async function setUpCardCheckBoxes() {
    const rules = await getRules()
    for (const rule in rules) {
        createCheckBox(rule, true, rule, rule)
    }
}

function createCheckBox(name, value, id, displayText) {
    var container = document.getElementById("checkBoxDiv")
    var checkboxContainer = document.createElement("div")
    checkboxContainer.className = "checkbox";

    var checkbox = document.createElement('input');
    checkbox.type = "checkbox";
    checkbox.name = name;
    checkbox.value = value;
    checkbox.id = id;

    var label = document.createElement('label')
    label.htmlFor = id;
    label.appendChild(document.createTextNode(displayText));

    checkboxContainer.appendChild(checkbox);
    checkboxContainer.appendChild(label);
    container.appendChild(checkboxContainer)
}

setUpCardCheckBoxes()