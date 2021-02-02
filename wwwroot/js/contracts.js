var numOfSelectedCredentials = 0;
var selectedRules = []

async function getRules() {

  try {
    const response = await fetch('/rules')
    await handleServiceErrors(response)
    const rules = await response.json()
    localStorage.setItem("rules", JSON.stringify(rules))
    console.log(rules)
    return rules

  } catch (error) {
    console.log("No Rules Found")
  }
}

function selectContract() {
  var selector = document.getElementById("selector" + numOfSelectedCredentials);
  var value = selector.options[selector.selectedIndex].value;
  var contracts = JSON.parse(localStorage.getItem("contracts"))
  selectedContracts.push(contracts[value])
  disableAddButtonAndSelector(numOfSelectedCredentials)
  removeContractChoice(contracts, value)
  numOfSelectedCredentials++
  addSelectBox()
}

function removeContractChoice(contracts, position) {
  contracts.splice(position, 1)
  localStorage.setItem("contracts", JSON.stringify(contracts))
}

function disableAddButtonAndSelector(idNum) {
  document.getElementById('addButton' + idNum).disabled = true
  document.getElementById('selector' + idNum).disabled = true
}

function addSelectBox() {
  var contracts = JSON.parse(localStorage.getItem("contracts"))
  if (contracts.length == 0) {
    alert("You added all of the contracts I have :)!")
    localStorage.removeItem("contracts")
    return
  }
  var parentDiv = document.getElementById("contractSelectorDiv");
  var selectElement = document.createElement("select");
  selectElement.className = "custom-select"
  selectElement.id = "selector" + numOfSelectedCredentials
  for (var i=0;i < contracts.length;i++) {
    var option = new Option (contracts[i].name, i);
    selectElement.options[selectElement.options.length] = option;
  }
  var selectButton = document.createElement('button')
  selectButton.onclick = selectContract
  selectButton.innerText = 'add'
  selectButton.type = "button"
  selectButton.className = "btn btn-outline-dark"
  selectButton.id = 'addButton' + numOfSelectedCredentials
  parentDiv.appendChild(selectElement);
  parentDiv.appendChild(selectButton)
}

getRules()